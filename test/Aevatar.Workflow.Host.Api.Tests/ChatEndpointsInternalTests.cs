using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatEndpointsInternalTests
{
    [Fact]
    public async Task HandleCommand_ShouldReturnAcceptedPayload_WhenDispatchSucceeds()
    {
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt("actor-1", "direct", "cmd-1", "corr-1")),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            service,
            new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory(),
            CancellationToken.None);

        service.LastCommand.Should().NotBeNull();
        result.GetType().Name.Should().StartWith("Accepted");
    }

    [Fact]
    public async Task HandleCommand_ShouldReturnMappedError_WhenDispatchFails()
    {
        var service = new FakeCommandDispatchService
        {
            Result = CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.WorkflowNotFound),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello", Workflow = "missing" },
            service,
            new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory(),
            CancellationToken.None);

        result.GetType().Name.Should().StartWith("JsonHttpResult");
        service.LastCommand.Should().NotBeNull();
        service.LastCommand!.WorkflowName.Should().Be("missing");
    }

    [Fact]
    public async Task HandleResume_ShouldDispatchWorkflowResumedEvent_WhenRunBindingMatches()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var bindingReader = new FakeWorkflowActorBindingReader
        {
            Binding = new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "actor-1",
                "definition-1",
                "run-1",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>()),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "approve-1",
                Approved = true,
                UserInput = "looks good",
                CommandId = "resume-cmd-1",
            },
            runtime,
            runtime,
            bindingReader,
            CancellationToken.None);

        result.GetType().Name.Should().StartWith("Ok");
        runtime.DispatchCalls.Should().ContainSingle();
        var resumed = runtime.DispatchCalls[0].Envelope.Payload!.Unpack<WorkflowResumedEvent>();
        resumed.RunId.Should().Be("run-1");
        resumed.StepId.Should().Be("approve-1");
        resumed.Approved.Should().BeTrue();
        resumed.UserInput.Should().Be("looks good");
        runtime.DispatchCalls[0].Envelope.Propagation!.CorrelationId.Should().Be("resume-cmd-1");
        runtime.DispatchCalls[0].Envelope.Runtime!.Deduplication!.OperationId.Should().Be("resume-cmd-1");
    }

    [Fact]
    public async Task HandleResume_ShouldReturnConflict_WhenRunBindingDoesNotMatch()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var bindingReader = new FakeWorkflowActorBindingReader
        {
            Binding = new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "actor-1",
                "definition-1",
                "run-bound",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>()),
        };

        var result = await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = "actor-1",
                RunId = "run-other",
                StepId = "approve-1",
            },
            runtime,
            runtime,
            bindingReader,
            CancellationToken.None);

        result.GetType().Name.Should().StartWith("Conflict");
        runtime.DispatchCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleSignal_ShouldDispatchSignalReceivedEvent_WithStepId()
    {
        var runtime = new FakeActorRuntime();
        runtime.StoredActors["actor-1"] = new FakeActor("actor-1");
        var bindingReader = new FakeWorkflowActorBindingReader
        {
            Binding = new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "actor-1",
                "definition-1",
                "run-1",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>()),
        };

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "run-1",
                StepId = "wait-approval",
                SignalName = "approval",
                Payload = "approved",
                CommandId = "signal-cmd-1",
            },
            runtime,
            runtime,
            bindingReader,
            CancellationToken.None);

        result.GetType().Name.Should().StartWith("Ok");
        runtime.DispatchCalls.Should().ContainSingle();
        var signal = runtime.DispatchCalls[0].Envelope.Payload!.Unpack<SignalReceivedEvent>();
        signal.RunId.Should().Be("run-1");
        signal.StepId.Should().Be("wait-approval");
        signal.SignalName.Should().Be("approval");
        signal.Payload.Should().Be("approved");
        runtime.DispatchCalls[0].Envelope.Propagation!.CorrelationId.Should().Be("signal-cmd-1");
        runtime.DispatchCalls[0].Envelope.Runtime!.Deduplication!.OperationId.Should().Be("signal-cmd-1");
    }

    [Fact]
    public async Task HandleSignal_ShouldReturnBadRequest_WhenRequiredFieldsMissing()
    {
        var runtime = new FakeActorRuntime();
        var bindingReader = new FakeWorkflowActorBindingReader();

        var result = await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = "actor-1",
                RunId = "",
                SignalName = "",
            },
            runtime,
            runtime,
            bindingReader,
            CancellationToken.None);

        result.GetType().Name.Should().StartWith("BadRequest");
        runtime.DispatchCalls.Should().BeEmpty();
    }

    private sealed class FakeCommandDispatchService
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> Result { get; set; } =
            CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.WorkflowNotFound);

        public Exception? DispatchException { get; set; }
        public WorkflowChatRunRequest? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            ct.ThrowIfCancellationRequested();
            if (DispatchException != null)
                throw DispatchException;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        public WorkflowActorBinding? Binding { get; set; }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Binding);
        }
    }

    private sealed class FakeActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public Dictionary<string, IActor> StoredActors { get; } = new(StringComparer.Ordinal);
        public List<(string ActorId, EventEnvelope Envelope)> DispatchCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(StoredActors.TryGetValue(id, out var actor) ? actor : null);

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DispatchCalls.Add((actorId, envelope));
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(StoredActors.ContainsKey(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new FakeAgent(id + "-agent");
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-agent");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
