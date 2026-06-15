using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
using DraftRunObservationScopeLeasePreparation = Aevatar.GAgentService.Abstractions.ScopeGAgents.GAgentDraftRunObservationScopeLeasePreparation;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunObservationScopeLeasePreparationTests
{
    [Fact]
    public void CommandContextSeed_ShouldExposeTypedCommandAndCorrelationSeeds()
    {
        var command = new GAgentDraftRunCommand(
            "scope-a",
            typeof(TestAgent).AssemblyQualifiedName!,
            "hello",
            CommandIdSeed: "cmd-seed",
            CorrelationIdSeed: "corr-seed",
            Headers: new Dictionary<string, string> { ["trace"] = "trace-1" });

        var seed = command.Should().BeAssignableTo<ICommandContextSeed>().Subject;
        seed.CommandId.Should().Be("cmd-seed");
        seed.CorrelationId.Should().Be("corr-seed");
        seed.Headers.Should().Contain("trace", "trace-1");
    }

    [Fact]
    public async Task PrepareAsync_ShouldPrepareObservationLeaseUsingExecutionContext()
    {
        var operations = new List<string>();
        var port = new RecordingPreparationPort(operations);
        var target = new GAgentDraftRunCommandTarget(
            new TestActor("draft-actor"),
            typeof(TestAgent).AssemblyQualifiedName!,
            new NoopDraftRunProjectionPort(),
            new NoopTerminalProjectionPort());
        var execution = new CommandDispatchExecution<GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt>
        {
            Target = target,
            Context = new CommandContext("draft-actor", "cmd-1", "corr-1", new Dictionary<string, string>()),
            Envelope = new EventEnvelope { Id = "env-1" },
            Receipt = new GAgentDraftRunAcceptedReceipt("draft-actor", typeof(TestAgent).AssemblyQualifiedName!, "cmd-1", "corr-1", "session-1"),
        };
        var preparation = new Aevatar.GAgentService.Application.ScopeGAgents.GAgentDraftRunObservationScopeLeasePreparation(port);

        var result = await preparation.PrepareAsync(
            new GAgentDraftRunCommand("scope-a", typeof(TestAgent).AssemblyQualifiedName!, "hello"),
            execution,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        port.Preparations.Should().ContainSingle().Which.Should().Be(("draft-actor", "cmd-1", "corr-1"));
        result.Handle.Should().NotBeNull();
        await result.Handle!.ReleaseAsync(CancellationToken.None);
        port.Released.Should().ContainSingle().Which.Should().Be(new DraftRunObservationScopeLeasePreparation("draft-actor", "cmd-1", "corr-1"));
        operations.Should().Equal("prepare:draft-actor", "release:draft-actor");
    }

    [Fact]
    public async Task PrepareAsync_ShouldReturnProjectionUnavailable_WhenPreparationFails()
    {
        var port = new RecordingPreparationPort { ReturnNull = true };
        var target = new GAgentDraftRunCommandTarget(
            new TestActor("draft-actor"),
            typeof(TestAgent).AssemblyQualifiedName!,
            new NoopDraftRunProjectionPort(),
            new NoopTerminalProjectionPort());
        var execution = new CommandDispatchExecution<GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt>
        {
            Target = target,
            Context = new CommandContext("draft-actor", "cmd-1", "corr-1", new Dictionary<string, string>()),
            Envelope = new EventEnvelope { Id = "env-1" },
            Receipt = new GAgentDraftRunAcceptedReceipt("draft-actor", typeof(TestAgent).AssemblyQualifiedName!, "cmd-1", "corr-1", "session-1"),
        };
        var preparation = new Aevatar.GAgentService.Application.ScopeGAgents.GAgentDraftRunObservationScopeLeasePreparation(port);

        var result = await preparation.PrepareAsync(
            new GAgentDraftRunCommand("scope-a", typeof(TestAgent).AssemblyQualifiedName!, "hello"),
            execution,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ProjectionUnavailable);
        result.Handle.Should().BeNull();
    }

    private sealed class RecordingPreparationPort(List<string>? operations = null)
        : IGAgentDraftRunObservationScopeLeasePreparationPort
    {
        public bool ReturnNull { get; init; }
        public List<(string ActorId, string CommandId, string CorrelationId)> Preparations { get; } = [];
        public List<DraftRunObservationScopeLeasePreparation> Released { get; } = [];

        public Task<DraftRunObservationScopeLeasePreparation?> PrepareAsync(
            string actorId,
            string commandId,
            string correlationId,
            CancellationToken ct = default)
        {
            operations?.Add($"prepare:{actorId}");
            Preparations.Add((actorId, commandId, correlationId));
            return Task.FromResult<DraftRunObservationScopeLeasePreparation?>(
                ReturnNull
                    ? null
                    : new DraftRunObservationScopeLeasePreparation(actorId, commandId, correlationId));
        }

        public Task ReleaseAsync(
            DraftRunObservationScopeLeasePreparation preparation,
            CancellationToken ct = default)
        {
            operations?.Add($"release:{preparation.ActorId}");
            Released.Add(preparation);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopDraftRunProjectionPort : IGAgentDraftRunProjectionPort
    {
        public bool ProjectionEnabled => true;

        public Task<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?> AttachExistingActorProjectionAsync(
            string actorId,
            string commandId,
            Aevatar.CQRS.Core.Abstractions.Streaming.IEventSink<AGUIEvent> sink,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            Aevatar.CQRS.Core.Abstractions.Streaming.IEventSink<AGUIEvent> sink,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ReleaseActorProjectionAsync(IGAgentDraftRunProjectionLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopTerminalProjectionPort : IGAgentRunTerminalProjectionPort
    {
        public Task<IGAgentRunTerminalProjectionLease?> AttachExistingProjectionAsync(
            string actorId,
            string correlationId,
            GAgentRunTerminalInteractionKind interactionKind,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ReleaseProjectionAsync(
            IGAgentRunTerminalProjectionLease lease,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new TestAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TestAgent : IAgent
    {
        public string Id { get; } = "test-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
