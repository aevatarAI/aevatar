using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.MultiAgent;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Aevatar.Interop.A2A.Application;
using FluentAssertions;

namespace Aevatar.Interop.A2A.Tests;

public class A2AAdapterServiceTests
{
    private readonly StubDispatchPort _dispatchPort = new();
    private readonly InMemoryA2ATaskStore _taskStore = new();
    private readonly A2AAdapterService _adapter;

    public A2AAdapterServiceTests()
    {
        _adapter = new A2AAdapterService(_dispatchPort, _taskStore);
    }

    private static Message MakeUserMessage(string text) => new()
    {
        Role = "user",
        Parts = [new TextPart { Text = text }],
    };

    // ─── SendTask tests ───

    [Fact]
    public async Task SendTask_WithAgentId_DispatchesAndSetsWorking()
    {
        var sendParams = new TaskSendParams
        {
            Id = "task-1",
            Message = MakeUserMessage("Hello agent"),
            Metadata = new() { ["agentId"] = "actor-123" },
        };

        var task = await _adapter.SendTaskAsync(sendParams);

        task.Id.Should().Be("task-1");
        task.Status.State.Should().Be(TaskState.Working);
        _dispatchPort.DispatchedCount.Should().Be(1);
        _dispatchPort.LastTargetActorId.Should().Be("actor-123");
    }

    [Fact]
    public async Task SendTask_WithSessionId_UsesAsActorId()
    {
        var sendParams = new TaskSendParams
        {
            Id = "task-2",
            SessionId = "session-actor-456",
            Message = MakeUserMessage("Hi"),
        };

        var task = await _adapter.SendTaskAsync(sendParams);

        task.Status.State.Should().Be(TaskState.Working);
        _dispatchPort.LastTargetActorId.Should().Be("session-actor-456");
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
    public async Task SendTask_DispatchFails_SetsFailedState()
    {
        _dispatchPort.ShouldThrow = true;
        var sendParams = new TaskSendParams
        {
            Id = "task-5",
            Message = MakeUserMessage("Hi"),
            Metadata = new() { ["agentId"] = "actor-1" },
        };

        var task = await _adapter.SendTaskAsync(sendParams);

        task.Status.State.Should().Be(TaskState.Failed);
    }

    // ─── GetTask tests ───

    [Fact]
    public async Task GetTask_Existing_ReturnsTask()
    {
        await _adapter.SendTaskAsync(new TaskSendParams
        {
            Id = "t1",
            Message = MakeUserMessage("Hello"),
            Metadata = new() { ["agentId"] = "a1" },
        });

        var task = await _adapter.GetTaskAsync(new TaskQueryParams { Id = "t1" });
        task.Should().NotBeNull();
        task!.Id.Should().Be("t1");
    }

    [Fact]
    public async Task GetTask_NonExistent_ReturnsNull()
    {
        var task = await _adapter.GetTaskAsync(new TaskQueryParams { Id = "missing" });
        task.Should().BeNull();
    }

    [Fact]
    public async Task GetTask_WithHistoryLength_TruncatesHistory()
    {
        await _adapter.SendTaskAsync(new TaskSendParams
        {
            Id = "t1",
            Message = MakeUserMessage("Hello"),
            Metadata = new() { ["agentId"] = "a1" },
        });

        var task = await _adapter.GetTaskAsync(new TaskQueryParams { Id = "t1", HistoryLength = 0 });
        task!.History.Should().BeEmpty();
    }

    // ─── CancelTask tests ───

    [Fact]
    public async Task CancelTask_WorkingTask_SetsCanceled()
    {
        await _adapter.SendTaskAsync(new TaskSendParams
        {
            Id = "t1",
            Message = MakeUserMessage("Hello"),
            Metadata = new() { ["agentId"] = "a1" },
        });

        var task = await _adapter.CancelTaskAsync(new TaskIdParams { Id = "t1" });
        task.Status.State.Should().Be(TaskState.Canceled);
    }

    [Fact]
    public async Task CancelTask_NonExistent_Throws()
    {
        var act = () => _adapter.CancelTaskAsync(new TaskIdParams { Id = "missing" });
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ─── AgentCard tests ───

    [Fact]
    public void GetAgentCard_ReturnsValidCard()
    {
        var card = _adapter.GetAgentCard("https://example.com");

        card.Name.Should().NotBeNullOrWhiteSpace();
        card.Url.Should().Be("https://example.com/a2a");
        card.Capabilities.Streaming.Should().BeTrue();
        card.Capabilities.StateTransitionHistory.Should().BeTrue();
        card.Skills.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SendTask_MultipleTextParts_JoinsWithNewline()
    {
        var sendParams = new TaskSendParams
        {
            Id = "task-multi",
            Message = new Message
            {
                Role = "user",
                Parts = [new TextPart { Text = "Hello" }, new TextPart { Text = "World" }],
            },
            Metadata = new() { ["agentId"] = "actor-1" },
        };

        var task = await _adapter.SendTaskAsync(sendParams);

        task.Status.State.Should().Be(TaskState.Working);
        _dispatchPort.LastPayloadContent.Should().Be("Hello\nWorld");
    }

    [Fact]
    public async Task GetTask_WithNegativeHistoryLength_ReturnsAllHistory()
    {
        await _adapter.SendTaskAsync(new TaskSendParams
        {
            Id = "t-neg",
            Message = MakeUserMessage("Hello"),
            Metadata = new() { ["agentId"] = "a1" },
        });

        var task = await _adapter.GetTaskAsync(new TaskQueryParams { Id = "t-neg", HistoryLength = -1 });
        task!.History.Should().NotBeEmpty("negative historyLength should not trim");
    }

    [Fact]
    public async Task GetTask_WithHistoryLengthExceedingCount_ReturnsAllHistory()
    {
        await _adapter.SendTaskAsync(new TaskSendParams
        {
            Id = "t-large",
            Message = MakeUserMessage("Hello"),
            Metadata = new() { ["agentId"] = "a1" },
        });

        var task = await _adapter.GetTaskAsync(new TaskQueryParams { Id = "t-large", HistoryLength = 100 });
        task!.History.Should().HaveCount(1, "historyLength > count returns all");
    }

    [Fact]
    public async Task CancelTask_CompletedTask_Throws()
    {
        await _adapter.SendTaskAsync(new TaskSendParams
        {
            Id = "t-done",
            Message = MakeUserMessage("Hello"),
            Metadata = new() { ["agentId"] = "a1" },
        });
        await _taskStore.UpdateTaskStateAsync("t-done", TaskState.Completed);

        var act = () => _adapter.CancelTaskAsync(new TaskIdParams { Id = "t-done" });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*terminal*");
    }

    [Fact]
    public async Task CancelTask_FailedTask_Throws()
    {
        _dispatchPort.ShouldThrow = true;
        await _adapter.SendTaskAsync(new TaskSendParams
        {
            Id = "t-fail",
            Message = MakeUserMessage("Hello"),
            Metadata = new() { ["agentId"] = "a1" },
        });

        var act = () => _adapter.CancelTaskAsync(new TaskIdParams { Id = "t-fail" });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*terminal*");
    }

    [Fact]
    public async Task SendTask_DispatchFails_PreservesExceptionMessage()
    {
        _dispatchPort.ShouldThrow = true;
        var sendParams = new TaskSendParams
        {
            Id = "task-err",
            Message = MakeUserMessage("Hi"),
            Metadata = new() { ["agentId"] = "actor-1" },
        };

        var task = await _adapter.SendTaskAsync(sendParams);

        task.Status.State.Should().Be(TaskState.Failed);
        task.Status.Message.Should().NotBeNull();
        var text = ((TextPart)task.Status.Message!.Parts[0]).Text;
        text.Should().Contain("Dispatch failed");
    }

    [Fact]
    public void GetAgentCard_TrailingSlash_NormalizesUrl()
    {
        var card = _adapter.GetAgentCard("https://example.com/");
        card.Url.Should().Be("https://example.com/a2a");
    }

    // ─── Stub ───

    private sealed class StubDispatchPort : IActorDispatchPort
    {
        public int DispatchedCount { get; private set; }
        public string? LastTargetActorId { get; private set; }
        public string? LastPayloadContent { get; private set; }
        public bool ShouldThrow { get; set; }

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (ShouldThrow) throw new InvalidOperationException("Dispatch failed");
            DispatchedCount++;
            LastTargetActorId = actorId;

            if (envelope.Payload != null)
            {
                var agentMessage = envelope.Payload.Unpack<AgentMessage>();
                LastPayloadContent = agentMessage.Content;
            }

            return Task.CompletedTask;
        }
    }
}
