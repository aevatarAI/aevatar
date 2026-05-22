using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Aevatar.Interop.A2A.Application;
using FluentAssertions;

namespace Aevatar.Interop.A2A.Tests;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: adapter tests asserted IA2ATaskStore process-local lifecycle mutation.
//   New principle: adapter tests assert typed command dispatch, readmodel query, and update subscription.
public class A2AAdapterServiceTests
{
    private readonly StubActorRuntime _runtime = new();
    private readonly StubDispatchPort _dispatchPort = new();
    private readonly StubProjectionReader _reader = new();
    private readonly StubActorEventSubscriptionProvider _subscriptionProvider = new();
    private readonly A2AAdapterService _adapter;

    public A2AAdapterServiceTests()
    {
        _adapter = new A2AAdapterService(_runtime, _dispatchPort, _reader, _subscriptionProvider);
    }

    [Fact]
    public async Task SendTask_WithAgentId_DispatchesSubmitCommandAndReturnsSubmittedReceipt()
    {
        var sendParams = new TaskSendParams
        {
            Id = "task-1",
            Message = MakeUserMessage("Hello agent"),
            Metadata = new() { ["agentId"] = "actor-123" },
        };

        var task = await _adapter.SendTaskAsync(sendParams);

        task.Id.Should().Be("task-1");
        task.Status.State.Should().Be(TaskState.Submitted);
        _runtime.CreatedActorIds.Should().Contain(A2ATaskActorId.Build("task-1"));
        _dispatchPort.LastTargetActorId.Should().Be(A2ATaskActorId.Build("task-1"));
        _dispatchPort.LastEnvelope!.Payload.Unpack<A2ATaskSubmitCommand>().TargetActorId.Should().Be("actor-123");
    }

    [Fact]
    public async Task SendTask_WithSessionId_UsesAsTargetActorId()
    {
        var sendParams = new TaskSendParams
        {
            Id = "task-2",
            SessionId = "session-actor-456",
            Message = MakeUserMessage("Hi"),
        };

        await _adapter.SendTaskAsync(sendParams);

        _dispatchPort.LastEnvelope!.Payload.Unpack<A2ATaskSubmitCommand>().TargetActorId
            .Should().Be("session-actor-456");
    }

    [Fact]
    public async Task SendTask_NoTargetId_Throws()
    {
        var sendParams = new TaskSendParams
        {
            Id = "task-3",
            Message = MakeUserMessage("Hi"),
        };

        var act = () => _adapter.SendTaskAsync(sendParams);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*agentId*");
    }

    [Fact]
    public async Task SendTask_EmptyMessage_Throws()
    {
        var sendParams = new TaskSendParams
        {
            Id = "task-4",
            Message = new Message { Role = "user", Parts = [] },
            Metadata = new() { ["agentId"] = "actor-1" },
        };

        var act = () => _adapter.SendTaskAsync(sendParams);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*text part*");
    }

    [Fact]
    public async Task SendTask_DispatchFails_PropagatesNoSyntheticFailedState()
    {
        _dispatchPort.ShouldThrow = true;
        var sendParams = new TaskSendParams
        {
            Id = "task-5",
            Message = MakeUserMessage("Hi"),
            Metadata = new() { ["agentId"] = "actor-1" },
        };

        var act = () => _adapter.SendTaskAsync(sendParams);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Dispatch failed");
    }

    [Fact]
    public async Task GetTask_ReadsReadModel()
    {
        _reader.Documents[A2ATaskActorId.Build("t1")] = MakeDocument("t1", TaskState.Working);

        var task = await _adapter.GetTaskAsync(new TaskQueryParams { Id = "t1" });

        task.Should().NotBeNull();
        task!.Id.Should().Be("t1");
        _reader.LastKey.Should().Be(A2ATaskActorId.Build("t1"));
    }

    [Fact]
    public async Task CancelTask_WorkingTask_DispatchesCancelCommandAndReturnsSubmittedReceipt()
    {
        _reader.Documents[A2ATaskActorId.Build("t1")] = MakeDocument("t1", TaskState.Working);

        var task = await _adapter.CancelTaskAsync(new TaskIdParams { Id = "t1" });

        task.Status.State.Should().Be(TaskState.Submitted);
        _dispatchPort.LastTargetActorId.Should().Be(A2ATaskActorId.Build("t1"));
        _dispatchPort.LastEnvelope!.Payload.Unpack<A2ATaskCancelCommand>().TaskId.Should().Be("t1");
    }

    [Fact]
    public async Task CancelTask_NonExistent_Throws()
    {
        var act = () => _adapter.CancelTaskAsync(new TaskIdParams { Id = "missing" });
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task CancelTask_TerminalTask_Throws()
    {
        _reader.Documents[A2ATaskActorId.Build("done")] = MakeDocument("done", TaskState.Completed);

        var act = () => _adapter.CancelTaskAsync(new TaskIdParams { Id = "done" });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*terminal*");
    }

    [Fact]
    public async Task SubscribeTaskUpdates_UsesTaskActorId()
    {
        await using var lease = await _adapter.SubscribeTaskUpdatesAsync("task-sub", _ => Task.CompletedTask);

        _subscriptionProvider.LastActorId.Should().Be(A2ATaskActorId.Build("task-sub"));
        lease.Should().NotBeNull();
    }

    [Fact]
    public void GetAgentCard_ReturnsValidCard()
    {
        var card = _adapter.GetAgentCard("https://example.com/");

        card.Url.Should().Be("https://example.com/a2a");
        card.Capabilities.Streaming.Should().BeTrue();
        card.Skills.Should().NotBeEmpty();
    }

    private static Message MakeUserMessage(string text) => new()
    {
        Role = "user",
        Parts = [new TextPart { Text = text }],
    };

    private static A2ATaskCurrentStateReadModel MakeDocument(string taskId, TaskState taskState)
    {
        var now = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow);
        var actorId = A2ATaskActorId.Build(taskId);
        var state = new A2ATaskState
        {
            TaskId = taskId,
            Status = A2ATaskModelMapper.BuildStatus(ToLifecycleState(taskState), now),
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = now,
        };
        state.History.Add(A2ATaskModelMapper.ToProto(MakeUserMessage("hello")));
        return new A2ATaskCurrentStateReadModel
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAtUtcValue = now,
            State = state,
        };
    }

    private static A2ATaskLifecycleState ToLifecycleState(TaskState state) =>
        state switch
        {
            TaskState.Submitted => A2ATaskLifecycleState.Submitted,
            TaskState.Working => A2ATaskLifecycleState.Working,
            TaskState.InputRequired => A2ATaskLifecycleState.InputRequired,
            TaskState.Completed => A2ATaskLifecycleState.Completed,
            TaskState.Canceled => A2ATaskLifecycleState.Canceled,
            TaskState.Failed => A2ATaskLifecycleState.Failed,
            _ => A2ATaskLifecycleState.Unknown,
        };

    private sealed class StubActorRuntime : IActorRuntime
    {
        public List<string> CreatedActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            CreatedActorIds.Add(id ?? agentType.Name);
            return Task.FromResult<IActor>(new StubActor(id ?? agentType.Name));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubDispatchPort : IActorDispatchPort
    {
        public string? LastTargetActorId { get; private set; }
        public EventEnvelope? LastEnvelope { get; private set; }
        public bool ShouldThrow { get; set; }

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (ShouldThrow) throw new InvalidOperationException("Dispatch failed");
            LastTargetActorId = actorId;
            LastEnvelope = envelope;
            return Task.CompletedTask;
        }
    }

    private sealed class StubProjectionReader : IProjectionDocumentReader<A2ATaskCurrentStateReadModel, string>
    {
        public Dictionary<string, A2ATaskCurrentStateReadModel> Documents { get; } = [];
        public string? LastKey { get; private set; }

        public Task<A2ATaskCurrentStateReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            LastKey = key;
            Documents.TryGetValue(key, out var document);
            return Task.FromResult(document);
        }

        public Task<ProjectionDocumentQueryResult<A2ATaskCurrentStateReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<A2ATaskCurrentStateReadModel>.Empty);
    }

    private sealed class StubActorEventSubscriptionProvider : IActorEventSubscriptionProvider
    {
        public string? LastActorId { get; private set; }

        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, Google.Protobuf.IMessage, new()
        {
            LastActorId = actorId;
            return Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        }
    }

    private sealed class StubActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new StubAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent : IAgent
    {
        public string Id => "stub-agent";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
