using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentApprovalInteractionTests
{
    [Fact]
    public async Task Resolver_ShouldReturnActorNotFound_WhenActorDoesNotExist()
    {
        var resolver = new GAgentApprovalCommandTargetResolver(
            new ApprovalStubActorRuntime(),
            new ApprovalProjectionPort());

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
            new ApprovalProjectionPort());

        var result = await resolver.ResolveAsync(
            new GAgentApprovalCommand(" actor-1 ", "req-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Target.Should().NotBeNull();
        result.Target!.Actor.Should().BeSameAs(actor);
        result.Target.ActorId.Should().Be("actor-1");
    }

    [Fact]
    public async Task Binder_ShouldBindProjectionLeaseAndLiveSink_WhenProjectionIsAvailable()
    {
        var projectionPort = new ApprovalProjectionPort
        {
            LeaseToReturn = new ApprovalProjectionLease("actor-1", "cmd-1"),
        };
        var binder = new GAgentApprovalCommandTargetBinder(projectionPort);
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            projectionPort);

        var result = await binder.BindAsync(
            new GAgentApprovalCommand("actor-1", "req-1"),
            target,
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        target.ProjectionLease.Should().BeSameAs(projectionPort.LeaseToReturn);
        target.LiveSink.Should().NotBeNull();
        projectionPort.EnsureCalls.Should().ContainSingle(x => x.actorId == "actor-1" && x.commandId == "cmd-1");
        projectionPort.AttachCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Binder_ShouldThrow_WhenProjectionPipelineIsUnavailable()
    {
        var projectionPort = new ApprovalProjectionPort
        {
            LeaseToReturn = null,
        };
        var binder = new GAgentApprovalCommandTargetBinder(projectionPort);
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            projectionPort);

        var act = async () => await binder.BindAsync(
            new GAgentApprovalCommand("actor-1", "req-1"),
            target,
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("GAgent approval projection pipeline is unavailable.");
        projectionPort.AttachCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupAfterDispatchFailureAsync_ShouldDetachReleaseAndDisposeBoundObservation()
    {
        var projectionPort = new ApprovalProjectionPort();
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            projectionPort);
        var sink = new RecordingAguiEventSink();
        var lease = new ApprovalProjectionLease("actor-1", "cmd-1");
        target.BindLiveObservation(lease, sink);

        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        projectionPort.DetachCalls.Should().ContainSingle(x => ReferenceEquals(x.lease, lease));
        projectionPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, lease));
        sink.Completed.Should().BeTrue();
        sink.DisposeCalls.Should().Be(1);
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public void RequireLiveSink_ShouldThrow_WhenObservationIsNotBound()
    {
        var target = new GAgentApprovalCommandTarget(
            new ApprovalStubActor("actor-1", new ApprovalStubAgent()),
            new ApprovalProjectionPort());

        var act = () => target.RequireLiveSink();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("GAgent approval live sink is not bound.");
    }

    [Fact]
    public void EnvelopeFactory_ShouldBuildDecisionEnvelope()
    {
        var factory = new GAgentApprovalCommandEnvelopeFactory();

        var envelope = factory.CreateEnvelope(
            new GAgentApprovalCommand("actor-1", "req-1", Approved: false, Reason: " deny ", SessionId: " session-1 "),
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()));

        var payload = envelope.Payload.Unpack<ToolApprovalDecisionEvent>();
        payload.RequestId.Should().Be("req-1");
        payload.SessionId.Should().Be("session-1");
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
            new ApprovalProjectionPort());
        var factory = new GAgentApprovalAcceptedReceiptFactory();

        var receipt = factory.Create(
            target,
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()));

        receipt.Should().Be(new GAgentApprovalAcceptedReceipt("actor-1", "cmd-1", "corr-1"));
    }

    [Fact]
    public void CompletionPolicy_ShouldResolveTerminalEvents()
    {
        var policy = new GAgentApprovalCompletionPolicy();

        policy.TryResolve(new AGUIEvent { TextMessageEnd = new Aevatar.Presentation.AGUI.TextMessageEndEvent() }, out var textCompletion)
            .Should().BeTrue();
        textCompletion.Should().Be(GAgentApprovalCompletionStatus.TextMessageCompleted);

        policy.TryResolve(new AGUIEvent { RunFinished = new RunFinishedEvent() }, out var runFinishedCompletion)
            .Should().BeTrue();
        runFinishedCompletion.Should().Be(GAgentApprovalCompletionStatus.RunFinished);

        policy.TryResolve(new AGUIEvent { RunError = new RunErrorEvent { Message = "boom" } }, out var failedCompletion)
            .Should().BeTrue();
        failedCompletion.Should().Be(GAgentApprovalCompletionStatus.Failed);

        policy.TryResolve(new AGUIEvent(), out var unknownCompletion).Should().BeFalse();
        unknownCompletion.Should().Be(GAgentApprovalCompletionStatus.Unknown);
        policy.IncompleteCompletion.Should().Be(GAgentApprovalCompletionStatus.Unknown);
    }

    [Fact]
    public async Task FinalizeEmitter_ShouldEmitRunFinished_OnlyForCompletedTextMessages()
    {
        var emitter = new GAgentApprovalFinalizeEmitter();
        var receipt = new GAgentApprovalAcceptedReceipt("actor-1", "cmd-1", "corr-1");
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
    public async Task DurableCompletionResolver_ShouldAlwaysReturnIncomplete()
    {
        var resolver = new GAgentApprovalDurableCompletionResolver();

        var result = await resolver.ResolveAsync(
            new GAgentApprovalAcceptedReceipt("actor-1", "cmd-1", "corr-1"),
            CancellationToken.None);

        result.Should().Be(CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>.Incomplete);
    }

    private sealed class ApprovalProjectionPort : IGAgentDraftRunProjectionPort
    {
        public ApprovalProjectionLease? LeaseToReturn { get; init; } = new("actor-1", "cmd-1");
        public bool ProjectionEnabled => true;
        public List<(string actorId, string commandId)> EnsureCalls { get; } = [];
        public List<(IGAgentDraftRunProjectionLease lease, IEventSink<AGUIEvent> sink)> AttachCalls { get; } = [];
        public List<(IGAgentDraftRunProjectionLease lease, IEventSink<AGUIEvent> sink)> DetachCalls { get; } = [];
        public List<IGAgentDraftRunProjectionLease> ReleaseCalls { get; } = [];

        public Task<IGAgentDraftRunProjectionLease?> EnsureActorProjectionAsync(
            string actorId,
            string commandId,
            CancellationToken ct = default)
        {
            EnsureCalls.Add((actorId, commandId));
            return Task.FromResult<IGAgentDraftRunProjectionLease?>(LeaseToReturn);
        }

        public Task AttachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            AttachCalls.Add((lease, sink));
            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            DetachCalls.Add((lease, sink));
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
}
