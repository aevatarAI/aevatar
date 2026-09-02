using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.GAgentService.Tests.Application;

// Test-add (test-coverage/cluster-035):
//   Covers refactor-introduced behavior in GAgentApprovalInteraction.cs:75-147.
//   Cluster intent: approval cleanup owns typed live-sink leases and detaches without a process registry.
public sealed class GAgentApprovalInteractionTests
{
    [Fact]
    public async Task Resolver_ShouldReturnActorNotFound_WhenActorDoesNotExist()
    {
        var resolver = new GAgentApprovalCommandTargetResolver(
            new ApprovalStubActorRuntime(),
            new ApprovalProjectionPort(),
            new ApprovalTerminalProjectionPort());

        var result = await resolver.ResolveAsync(
            new GAgentApprovalCommand("actor-1", "req-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentApprovalStartError.ActorNotFound);
    }

    [Fact]
    public async Task Resolver_ShouldReturnTarget_WhenActorExists()
    {
        var actor = new ApprovalStubActor("actor-1", new ApprovalStubAgent());
        var resolver = new GAgentApprovalCommandTargetResolver(
            new ApprovalStubActorRuntime(actor),
            new ApprovalProjectionPort(),
            new ApprovalTerminalProjectionPort());

        var result = await resolver.ResolveAsync(
            new GAgentApprovalCommand(" actor-1 ", "req-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.Actor.Should().BeSameAs(actor);
        result.Target.ActorId.Should().Be("actor-1");
    }

    [Fact]
    public async Task ObservationLifecycle_ShouldBindProjectionLeaseAndLiveSink_WhenProjectionIsAvailable()
    {
        var projectionPort = new ApprovalProjectionPort
        {
            LeaseToReturn = new ApprovalProjectionLease("actor-1", "cmd-1"),
        };
        var terminalPort = new ApprovalTerminalProjectionPort();
        var lifecycle = new GAgentApprovalObservationLifecycle(projectionPort, terminalPort);
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            projectionPort,
            terminalPort);
        var context = new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>());

        var result = await lifecycle.BindAsync(
            new GAgentApprovalCommand("actor-1", "req-1", SessionId: "legacy-session"),
            CreateExecution(target, context),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        target.ProjectionLease.Should().BeSameAs(projectionPort.LeaseToReturn);
        target.LiveSinkLease.Should().BeSameAs(projectionPort.LiveSinkLeaseToReturn);
        target.LiveSink.Should().NotBeNull();
        target.ContinuationTurnId.Should().StartWith("turn-").And.NotBe("legacy-session");
        projectionPort.AttachCalls.Should().ContainSingle();
        terminalPort.Calls.Should().ContainSingle(x =>
            x.actorId == "actor-1" &&
            x.correlationId == "corr-1" &&
            x.interactionKind == GAgentRunTerminalInteractionKind.Approval);
    }

    [Fact]
    public async Task ObservationLifecycle_ShouldReturnProjectionUnavailable_WhenProjectionPipelineIsUnavailable()
    {
        var projectionPort = new ApprovalProjectionPort
        {
            LeaseToReturn = null,
        };
        var terminalPort = new ApprovalTerminalProjectionPort();
        var lifecycle = new GAgentApprovalObservationLifecycle(projectionPort, terminalPort);
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            projectionPort,
            terminalPort);
        var context = new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>());

        var result = await lifecycle.BindAsync(
            new GAgentApprovalCommand("actor-1", "req-1"),
            CreateExecution(target, context),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentApprovalStartError.ProjectionUnavailable);
        projectionPort.AttachCalls.Should().BeEmpty();
        terminalPort.Calls.Should().ContainSingle(x =>
            x.actorId == "actor-1" &&
            x.correlationId == "corr-1" &&
            x.interactionKind == GAgentRunTerminalInteractionKind.Approval);
        terminalPort.ReleaseCalls.Should().ContainSingle();
    }

    private static CommandDispatchExecution<GAgentApprovalCommandTarget, GAgentApprovalAcceptedReceipt> CreateExecution(
        GAgentApprovalCommandTarget target,
        CommandContext context) =>
        new()
        {
            Target = target,
            Context = context,
            Envelope = new EventEnvelope { Id = "evt-1" },
            Receipt = new GAgentApprovalAcceptedReceipt(target.ActorId, context.CommandId, context.CorrelationId, string.Empty),
        };

    [Fact]
    public async Task CleanupAfterDispatchFailureAsync_ShouldDetachReleaseAndDisposeBoundObservation()
    {
        var projectionPort = new ApprovalProjectionPort();
        var terminalPort = new ApprovalTerminalProjectionPort();
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            projectionPort,
            terminalPort);
        var sink = new RecordingAguiEventSink();
        var lease = new ApprovalProjectionLease("actor-1", "cmd-1");
        var terminalLease = new ApprovalTerminalProjectionLease(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.Approval);
        target.BindTerminalProjection(terminalLease);
        var liveSinkLease = new RecordingLiveSinkLease();
        target.BindLiveObservation(lease, liveSinkLease, sink);

        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        projectionPort.DetachedLiveSinkLeases.Should().ContainSingle(x => ReferenceEquals(x, liveSinkLease));
        liveSinkLease.DisposeCount.Should().Be(1);
        projectionPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, lease));
        sink.Completed.Should().BeTrue();
        sink.DisposeCalls.Should().Be(1);
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
        terminalPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, terminalLease));
        target.TerminalProjectionLease.Should().BeNull();
    }

    [Fact]
    public async Task CleanupAfterDispatchFailureAsync_WhenOnlySinkIsBound_ShouldCompleteDisposeAndSkipProjectionDetach()
    {
        var projectionPort = new ApprovalProjectionPort();
        var terminalPort = new ApprovalTerminalProjectionPort();
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            projectionPort,
            terminalPort);
        var sink = new RecordingAguiEventSink();
        var terminalLease = new ApprovalTerminalProjectionLease(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.Approval);
        target.BindTerminalProjection(terminalLease);
        target.BindLiveObservation(new ApprovalProjectionLease("actor-1", "cmd-1"), new RecordingLiveSinkLease(), sink);
        SetProperty(target, nameof(GAgentApprovalCommandTarget.ProjectionLease), null);

        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        projectionPort.DetachedLiveSinkLeases.Should().BeEmpty();
        projectionPort.ReleaseCalls.Should().BeEmpty();
        sink.Completed.Should().BeTrue();
        sink.DisposeCalls.Should().Be(1);
        target.LiveSink.Should().BeNull();
        target.LiveSinkLease.Should().BeNull();
        terminalPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, terminalLease));
    }

    [Fact]
    public async Task CleanupAfterDispatchFailureAsync_WhenOnlyProjectionLeaseIsBound_ShouldReleaseLeaseAndSkipProjectionDetach()
    {
        var projectionPort = new ApprovalProjectionPort();
        var terminalPort = new ApprovalTerminalProjectionPort();
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            projectionPort,
            terminalPort);
        var lease = new ApprovalProjectionLease("actor-1", "cmd-1");
        var terminalLease = new ApprovalTerminalProjectionLease(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.Approval);
        target.BindTerminalProjection(terminalLease);
        target.BindLiveObservation(lease, new RecordingLiveSinkLease(), new RecordingAguiEventSink());
        SetProperty(target, nameof(GAgentApprovalCommandTarget.LiveSink), null);

        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        projectionPort.DetachedLiveSinkLeases.Should().BeEmpty();
        projectionPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, lease));
        target.ProjectionLease.Should().BeNull();
        target.LiveSinkLease.Should().BeNull();
        terminalPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, terminalLease));
    }

    [Fact]
    public void RequireLiveSink_ShouldThrow_WhenObservationIsNotBound()
    {
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            new ApprovalProjectionPort(),
            new ApprovalTerminalProjectionPort());

        var act = () => target.RequireLiveSink();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("GAgent approval live sink is not bound.");
    }

    [Fact]
    public void EnvelopeFactory_ShouldBuildDecisionEnvelope()
    {
        var factory = new GAgentApprovalCommandEnvelopeFactory();
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            new ApprovalProjectionPort(),
            new ApprovalTerminalProjectionPort());

        var envelope = factory.CreateEnvelope(
            new GAgentApprovalCommand("actor-1", "req-1", Approved: false, Reason: " deny ", SessionId: " session-1 "),
            target,
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()));

        var payload = envelope.Payload.Unpack<ToolApprovalDecisionEvent>();
        target.ContinuationTurnId.Should().StartWith("turn-").And.NotBe("session-1");
        payload.RequestId.Should().Be("req-1");
        payload.ContinuationTurnId.Should().Be(target.ContinuationTurnId);
        payload.Approved.Should().BeFalse();
        payload.Reason.Should().Be("deny");
        envelope.Route.GetTargetActorId().Should().Be("actor-1");
        envelope.Propagation.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public void ReceiptFactory_ShouldCreateAcceptedReceipt()
    {
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            new ApprovalProjectionPort(),
            new ApprovalTerminalProjectionPort());
        var factory = new GAgentApprovalAcceptedReceiptFactory();

        var receipt = factory.Create(
            target,
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()));

        target.ContinuationTurnId.Should().StartWith("turn-");
        receipt.Should().Be(new GAgentApprovalAcceptedReceipt("actor-1", "cmd-1", "corr-1", target.ContinuationTurnId));
    }

    [Fact]
    public void CompletionPolicy_ShouldResolveTerminalEvents()
    {
        var policy = new GAgentApprovalCompletionPolicy();

        policy.TryResolve(new AGUIEvent { TextMessageEnd = new Aevatar.AGUI.Contracts.TextMessageEndEvent() }, out var textCompletion)
            .Should().BeTrue();
        textCompletion.Should().Be(GAgentApprovalCompletionStatus.TextMessageCompleted);

        policy.TryResolve(new AGUIEvent { RunFinished = new RunFinishedEvent() }, out var runFinishedCompletion)
            .Should().BeTrue();
        runFinishedCompletion.Should().Be(GAgentApprovalCompletionStatus.RunFinished);

        policy.TryResolve(new AGUIEvent { RunError = new RunErrorEvent { Message = "boom" } }, out var failedCompletion)
            .Should().BeTrue();
        failedCompletion.Should().Be(GAgentApprovalCompletionStatus.Failed);

        policy.TryResolve(
                new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Code = GAgentRunFailureCodes.OutcomeUncertain,
                        Message = "The interrupted session may have produced side effects.",
                    },
                },
                out var uncertainCompletion)
            .Should().BeTrue();
        uncertainCompletion.Should().Be(GAgentApprovalCompletionStatus.OutcomeUncertain);

        policy.TryResolve(new AGUIEvent(), out var unknownCompletion).Should().BeFalse();
        unknownCompletion.Should().Be(GAgentApprovalCompletionStatus.Unknown);
        policy.IncompleteCompletion.Should().Be(GAgentApprovalCompletionStatus.Unknown);
    }

    [Fact]
    public async Task FinalizeEmitter_ShouldEmitRunFinished_OnlyForCompletedTextMessages()
    {
        var emitter = new GAgentApprovalFinalizeEmitter();
        var receipt = new GAgentApprovalAcceptedReceipt("actor-1", "cmd-1", "corr-1", "session-1");
        var emitted = new List<AGUIEvent>();

        await emitter.EmitAsync(
            receipt,
            GAgentApprovalCompletionStatus.TextMessageCompleted,
            completed: true,
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        emitted.Should().ContainSingle();
        emitted[0].RunFinished.ThreadId.Should().Be("actor-1");
        emitted[0].RunFinished.RunId.Should().Be("cmd-1");

        emitted.Clear();
        await emitter.EmitAsync(
            receipt,
            GAgentApprovalCompletionStatus.RunFinished,
            completed: true,
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        emitted.Should().BeEmpty();
    }

    [Fact]
    public async Task DurableCompletionResolver_ShouldResolveTerminalSnapshot()
    {
        var queryPort = new ApprovalTerminalQueryPort
        {
            CorrelationSnapshot = new GAgentRunTerminalSnapshot(
                "actor-1",
                "session-1",
                "corr-1",
                GAgentRunTerminalInteractionKind.Approval,
                GAgentRunTerminalStatus.Failed,
                "approval_denied",
                "denied",
                2,
                "evt-1",
                DateTimeOffset.UtcNow),
        };
        var resolver = new GAgentApprovalDurableCompletionResolver(queryPort);

        var result = await resolver.ResolveAsync(
            new GAgentApprovalAcceptedReceipt("actor-1", "cmd-1", "corr-1", "session-1"),
            CancellationToken.None);

        result.Should().Be(new CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>(
            true,
            GAgentApprovalCompletionStatus.Failed));
        queryPort.CorrelationCalls.Should().ContainSingle(x => x.actorId == "actor-1" && x.correlationId == "corr-1");
    }

    [Fact]
    public async Task DurableCompletionResolver_ShouldResolveOutcomeUncertainAsCompleted()
    {
        var queryPort = new ApprovalTerminalQueryPort
        {
            CorrelationSnapshot = new GAgentRunTerminalSnapshot(
                "actor-1",
                "session-1",
                "corr-1",
                GAgentRunTerminalInteractionKind.Approval,
                GAgentRunTerminalStatus.OutcomeUncertain,
                GAgentRunFailureCodes.OutcomeUncertain,
                "The interrupted session may have produced side effects.",
                3,
                "evt-uncertain",
                DateTimeOffset.UtcNow),
        };
        var resolver = new GAgentApprovalDurableCompletionResolver(queryPort);

        var result = await resolver.ResolveAsync(
            new GAgentApprovalAcceptedReceipt("actor-1", "cmd-1", "corr-1", "session-1"),
            CancellationToken.None);

        result.Should().Be(new CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>(
            true,
            GAgentApprovalCompletionStatus.OutcomeUncertain));
    }

    [Fact]
    public async Task DurableCompletionResolver_ShouldIgnoreSessionFallback_WhenCorrelationDiffers()
    {
        var queryPort = new ApprovalTerminalQueryPort
        {
            SessionSnapshot = new GAgentRunTerminalSnapshot(
                "actor-1",
                "session-1",
                "old-corr",
                GAgentRunTerminalInteractionKind.Approval,
                GAgentRunTerminalStatus.Failed,
                "approval_denied",
                "denied",
                2,
                "evt-1",
                DateTimeOffset.UtcNow),
        };
        var resolver = new GAgentApprovalDurableCompletionResolver(queryPort);

        var result = await resolver.ResolveAsync(
            new GAgentApprovalAcceptedReceipt("actor-1", "cmd-1", "corr-1", "session-1"),
            CancellationToken.None);

        result.Should().Be(CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>.Incomplete);
        queryPort.SessionCalls.Should().ContainSingle(x => x.actorId == "actor-1" && x.sessionId == "session-1");
    }

    [Fact]
    public async Task DurableCompletionResolver_ShouldIgnoreSessionFallback_WhenInteractionKindDiffers()
    {
        var queryPort = new ApprovalTerminalQueryPort
        {
            SessionSnapshot = new GAgentRunTerminalSnapshot(
                "actor-1",
                "session-1",
                "corr-1",
                GAgentRunTerminalInteractionKind.DraftRun,
                GAgentRunTerminalStatus.Failed,
                string.Empty,
                string.Empty,
                2,
                "evt-1",
                DateTimeOffset.UtcNow),
        };
        var resolver = new GAgentApprovalDurableCompletionResolver(queryPort);

        var result = await resolver.ResolveAsync(
            new GAgentApprovalAcceptedReceipt("actor-1", "cmd-1", "corr-1", "session-1"),
            CancellationToken.None);

        result.Should().Be(CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>.Incomplete);
    }

    [Fact]
    public async Task DurableCompletionResolver_ShouldUseSessionFallback_WhenReceiptMatches()
    {
        var queryPort = new ApprovalTerminalQueryPort
        {
            SessionSnapshot = new GAgentRunTerminalSnapshot(
                "actor-1",
                "session-1",
                "corr-1",
                GAgentRunTerminalInteractionKind.Approval,
                GAgentRunTerminalStatus.RunFinished,
                string.Empty,
                string.Empty,
                2,
                "evt-1",
                DateTimeOffset.UtcNow),
        };
        var resolver = new GAgentApprovalDurableCompletionResolver(queryPort);

        var result = await resolver.ResolveAsync(
            new GAgentApprovalAcceptedReceipt("actor-1", "cmd-1", "corr-1", "session-1"),
            CancellationToken.None);

        result.Should().Be(new CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>(
            true,
            GAgentApprovalCompletionStatus.RunFinished));
    }

    private sealed class ApprovalProjectionPort : IGAgentDraftRunProjectionPort
    {
        public ApprovalProjectionLease? LeaseToReturn { get; init; } = new("actor-1", "cmd-1");
        public RecordingLiveSinkLease LiveSinkLeaseToReturn { get; } = new();
        public bool ProjectionEnabled => true;
        public List<(IGAgentDraftRunProjectionLease lease, IEventSink<AGUIEvent> sink)> AttachCalls { get; } = [];
        public List<IAsyncDisposable?> DetachedLiveSinkLeases { get; } = [];
        public List<IGAgentDraftRunProjectionLease> ReleaseCalls { get; } = [];

        public async Task<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?> AttachExistingActorProjectionAsync(
            string actorId,
            string commandId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            if (LeaseToReturn == null)
                return null;

            var liveSinkLease = await AttachLiveSinkAsync(LeaseToReturn, sink, ct);
            return new EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>(LeaseToReturn, liveSinkLease);
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            AttachCalls.Add((lease, sink));
            return Task.FromResult<IAsyncDisposable?>(LiveSinkLeaseToReturn);
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            DetachedLiveSinkLeases.Add(liveSinkLease);
            if (liveSinkLease != null)
            {
                return liveSinkLease.DisposeAsync().AsTask();
            }

            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IGAgentDraftRunProjectionLease lease,
            CancellationToken ct = default)
        {
            ReleaseCalls.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed record ApprovalProjectionLease(string ActorId, string CommandId) : IGAgentDraftRunProjectionLease;

    private sealed class RecordingLiveSinkLease : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ApprovalTerminalProjectionPort : IGAgentRunTerminalProjectionPort
    {
        public List<(string actorId, string correlationId, GAgentRunTerminalInteractionKind interactionKind)> Calls { get; } = [];
        public List<IGAgentRunTerminalProjectionLease> ReleaseCalls { get; } = [];

        public Task<IGAgentRunTerminalProjectionLease?> AttachExistingProjectionAsync(
            string actorId,
            string correlationId,
            GAgentRunTerminalInteractionKind interactionKind,
            CancellationToken ct = default)
        {
            Calls.Add((actorId, correlationId, interactionKind));
            return Task.FromResult<IGAgentRunTerminalProjectionLease?>(
                new ApprovalTerminalProjectionLease(actorId, correlationId, interactionKind));
        }

        public Task ReleaseProjectionAsync(
            IGAgentRunTerminalProjectionLease lease,
            CancellationToken ct = default)
        {
            ReleaseCalls.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed record ApprovalTerminalProjectionLease(
        string ActorId,
        string CorrelationId,
        GAgentRunTerminalInteractionKind InteractionKind) : IGAgentRunTerminalProjectionLease;

    private sealed class ApprovalTerminalQueryPort : IGAgentRunTerminalQueryPort
    {
        public GAgentRunTerminalSnapshot? CorrelationSnapshot { get; init; }
        public GAgentRunTerminalSnapshot? SessionSnapshot { get; init; }
        public List<(string actorId, string correlationId)> CorrelationCalls { get; } = [];
        public List<(string actorId, string sessionId)> SessionCalls { get; } = [];

        public Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
            string actorId,
            string correlationId,
            CancellationToken ct = default)
        {
            CorrelationCalls.Add((actorId, correlationId));
            return Task.FromResult(CorrelationSnapshot);
        }

        public Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default)
        {
            SessionCalls.Add((actorId, sessionId));
            return Task.FromResult(SessionSnapshot);
        }
    }

    private sealed class ApprovalStubActorRuntime(params IActor[] actors) : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = actors.ToDictionary(x => x.Id, StringComparer.Ordinal);

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ApprovalStubActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class ApprovalStubAgent : IAgent
    {
        public string Id => "approval-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingAguiEventSink : IEventSink<AGUIEvent>
    {
        public bool Completed { get; private set; }
        public int DisposeCalls { get; private set; }
        public List<AGUIEvent> Events { get; } = [];

        public void Push(AGUIEvent evt) => Events.Add(evt);

        public ValueTask PushAsync(AGUIEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete() => Completed = true;

        public async IAsyncEnumerable<AGUIEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private static void SetProperty(object instance, string propertyName, object? value)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        property.Should().NotBeNull();
        property!.SetValue(instance, value);
    }
}
