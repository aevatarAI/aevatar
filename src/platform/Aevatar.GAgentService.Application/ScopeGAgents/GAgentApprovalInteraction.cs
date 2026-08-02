using System.Runtime.ExceptionServices;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.AGUI.Contracts;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.ScopeGAgents;

internal sealed class GAgentApprovalCommandTarget
    : IActorCommandDispatchTarget,
      ICommandEventTarget<AGUIEvent>,
      ICommandInteractionCleanupTarget<GAgentApprovalAcceptedReceipt, GAgentApprovalCompletionStatus>,
      ICommandDispatchCleanupAware
{
    private readonly IGAgentDraftRunProjectionPort _projectionPort;
    private readonly IGAgentRunTerminalProjectionPort _terminalProjectionPort;

    public GAgentApprovalCommandTarget(
        IActor actor,
        IGAgentDraftRunProjectionPort projectionPort,
        IGAgentRunTerminalProjectionPort terminalProjectionPort)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _terminalProjectionPort = terminalProjectionPort ?? throw new ArgumentNullException(nameof(terminalProjectionPort));
    }

    public IActor Actor { get; }
    public string TargetId => Actor.Id;
    public string ActorId => Actor.Id;
    public string ContinuationTurnId { get; } = $"turn-{Guid.NewGuid():N}";
    public IGAgentDraftRunProjectionLease? ProjectionLease { get; private set; }
    public IGAgentRunTerminalProjectionLease? TerminalProjectionLease { get; private set; }
    public IAsyncDisposable? LiveSinkLease { get; private set; }
    public IEventSink<AGUIEvent>? LiveSink { get; private set; }

    public void BindTerminalProjection(IGAgentRunTerminalProjectionLease? lease)
    {
        TerminalProjectionLease = lease;
    }

    public void BindLiveObservation(
        IGAgentDraftRunProjectionLease lease,
        IAsyncDisposable? liveSinkLease,
        IEventSink<AGUIEvent> sink)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: command preparation could attach projection/session leases and mix read-side observation into dispatch admission.
        //   New principle: live observation is an explicit interaction phase that starts before dispatch; PrepareAsync and dispatch-only callers stay free of read-side lifecycle work
        ProjectionLease = lease ?? throw new ArgumentNullException(nameof(lease));
        LiveSinkLease = liveSinkLease;
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IEventSink<AGUIEvent> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("GAgent approval live sink is not bound.");

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        ReleaseAsync(ct);

    public Task ReleaseAfterInteractionAsync(
        GAgentApprovalAcceptedReceipt receipt,
        CommandInteractionCleanupContext<GAgentApprovalCompletionStatus> cleanup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(cleanup);
        return ReleaseAsync(ct);
    }

    private async Task ReleaseAsync(CancellationToken ct)
    {
        Exception? firstException = null;
        var projectionLease = ProjectionLease;
        var sink = LiveSink;

        if (projectionLease != null && sink != null)
        {
            try
            {
                await _projectionPort.DetachReleaseAndDisposeAsync(
                    projectionLease,
                    LiveSinkLease,
                    sink,
                    null,
                    ct);
                ProjectionLease = null;
                LiveSinkLease = null;
                LiveSink = null;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }
        else
        {
            if (sink != null)
            {
                try
                {
                    sink.Complete();
                    await sink.DisposeAsync();
                    LiveSinkLease = null;
                    LiveSink = null;
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
            }

            if (projectionLease != null)
            {
                try
                {
                    await _projectionPort.ReleaseActorProjectionAsync(projectionLease, ct);
                    ProjectionLease = null;
                    LiveSinkLease = null;
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
            }
        }

        var terminalProjectionLease = TerminalProjectionLease;
        if (terminalProjectionLease != null)
        {
            try
            {
                await _terminalProjectionPort.ReleaseProjectionAsync(terminalProjectionLease, ct);
                TerminalProjectionLease = null;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }
}

internal sealed class GAgentApprovalCommandTargetResolver
    : ICommandTargetResolver<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalStartError>
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IGAgentDraftRunProjectionPort _projectionPort;
    private readonly IGAgentRunTerminalProjectionPort _terminalProjectionPort;

    public GAgentApprovalCommandTargetResolver(
        IActorRuntime actorRuntime,
        IGAgentDraftRunProjectionPort projectionPort,
        IGAgentRunTerminalProjectionPort terminalProjectionPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _terminalProjectionPort = terminalProjectionPort ?? throw new ArgumentNullException(nameof(terminalProjectionPort));
    }

    public async Task<CommandTargetResolution<GAgentApprovalCommandTarget, GAgentApprovalStartError>> ResolveAsync(
        GAgentApprovalCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var actor = await _actorRuntime.GetAsync(command.ActorId.Trim());
        if (actor == null)
        {
            return CommandTargetResolution<GAgentApprovalCommandTarget, GAgentApprovalStartError>.Failure(
                GAgentApprovalStartError.ActorNotFound);
        }

        return CommandTargetResolution<GAgentApprovalCommandTarget, GAgentApprovalStartError>.Success(
            new GAgentApprovalCommandTarget(actor, _projectionPort, _terminalProjectionPort));
    }
}

internal sealed class GAgentApprovalObservationLifecycle
    : ICommandObservationLifecycle<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalAcceptedReceipt, GAgentApprovalStartError>
{
    private readonly IGAgentDraftRunProjectionPort _projectionPort;
    private readonly IGAgentRunTerminalProjectionPort _terminalProjectionPort;

    public GAgentApprovalObservationLifecycle(
        IGAgentDraftRunProjectionPort projectionPort,
        IGAgentRunTerminalProjectionPort terminalProjectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _terminalProjectionPort = terminalProjectionPort ?? throw new ArgumentNullException(nameof(terminalProjectionPort));
    }

    public async Task<CommandObservationBindingResult<GAgentApprovalStartError>> BindAsync(
        GAgentApprovalCommand command,
        CommandDispatchExecution<GAgentApprovalCommandTarget, GAgentApprovalAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        // Refactor (iter37/cluster-037-gagentservice-binders-attach-existing):
        //   Old pattern: GAgentService interaction binders synchronously prime projection sessions before dispatch(request-path projection activation in BindAsync).
        //   New principle: Attach-only to existing projection sessions/materialization leases via capability-specific attach-existing ports.
        //   Cold sessions return ProjectionUnavailable / pending before dispatch; no top-level live-observation exception.
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var context = execution.Context;
        var sink = new EventChannel<AGUIEvent>();
        IGAgentRunTerminalProjectionLease? terminalProjectionLease = null;

        try
        {
            terminalProjectionLease = await _terminalProjectionPort.AttachExistingProjectionAsync(
                target.ActorId,
                context.CorrelationId,
                GAgentRunTerminalInteractionKind.Approval,
                ct);
            if (terminalProjectionLease == null)
                return await FailProjectionUnavailableAsync(sink);

            target.BindTerminalProjection(terminalProjectionLease);

            var attachment = await _projectionPort.AttachExistingActorProjectionAsync(
                target.ActorId,
                context.CorrelationId,
                sink,
                ct);

            if (attachment == null)
            {
                await _terminalProjectionPort.ReleaseProjectionAsync(terminalProjectionLease, ct);
                target.BindTerminalProjection(null);
                return await FailProjectionUnavailableAsync(sink);
            }

            target.BindLiveObservation(
                attachment.ProjectionLease,
                attachment.LiveSinkLease,
                sink);
            return CommandObservationBindingResult<GAgentApprovalStartError>.Success();
        }
        catch
        {
            if (terminalProjectionLease != null)
            {
                await _terminalProjectionPort.ReleaseProjectionAsync(terminalProjectionLease, ct);
                target.BindTerminalProjection(null);
            }

            sink.Complete();
            await sink.DisposeAsync();
            throw;
        }
    }

    private static async Task<CommandObservationBindingResult<GAgentApprovalStartError>> FailProjectionUnavailableAsync(
        IEventSink<AGUIEvent> sink)
    {
        sink.Complete();
        await sink.DisposeAsync();
        return CommandObservationBindingResult<GAgentApprovalStartError>.Failure(
            GAgentApprovalStartError.ProjectionUnavailable);
    }
}

internal sealed class GAgentApprovalCommandEnvelopeFactory
    : ICommandTargetEnvelopeFactory<GAgentApprovalCommand, GAgentApprovalCommandTarget>
{
    public EventEnvelope CreateEnvelope(
        GAgentApprovalCommand command,
        GAgentApprovalCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var decisionEvent = new ToolApprovalDecisionEvent
        {
            RequestId = command.RequestId,
            ContinuationTurnId = target.ContinuationTurnId,
            Approved = command.Approved,
            Reason = command.Reason?.Trim() ?? string.Empty,
        };

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(decisionEvent),
            Route = EnvelopeRouteSemantics.CreateDirect("api", context.TargetId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }
}

internal sealed class GAgentApprovalAcceptedReceiptFactory
    : ICommandReceiptFactory<GAgentApprovalCommandTarget, GAgentApprovalAcceptedReceipt>
{
    public GAgentApprovalAcceptedReceipt Create(
        GAgentApprovalCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new GAgentApprovalAcceptedReceipt(
            target.ActorId,
            context.CommandId,
            context.CorrelationId,
            target.ContinuationTurnId);
    }
}

internal sealed class GAgentApprovalCompletionPolicy
    : ICommandCompletionPolicy<AGUIEvent, GAgentApprovalCompletionStatus>
{
    public GAgentApprovalCompletionStatus IncompleteCompletion => GAgentApprovalCompletionStatus.Unknown;

    public bool TryResolve(
        AGUIEvent evt,
        out GAgentApprovalCompletionStatus completion)
    {
        ArgumentNullException.ThrowIfNull(evt);

        completion = GAgentApprovalCompletionStatus.Unknown;
        switch (evt.EventCase)
        {
            case AGUIEvent.EventOneofCase.TextMessageEnd:
                completion = GAgentApprovalCompletionStatus.TextMessageCompleted;
                return true;
            case AGUIEvent.EventOneofCase.RunFinished:
                completion = GAgentApprovalCompletionStatus.RunFinished;
                return true;
            case AGUIEvent.EventOneofCase.RunError:
                completion = string.Equals(
                    evt.RunError.Code,
                    GAgentRunFailureCodes.OutcomeUncertain,
                    StringComparison.Ordinal)
                    ? GAgentApprovalCompletionStatus.OutcomeUncertain
                    : GAgentApprovalCompletionStatus.Failed;
                return true;
            default:
                return false;
        }
    }
}

internal sealed class GAgentApprovalFinalizeEmitter
    : ICommandFinalizeEmitter<GAgentApprovalAcceptedReceipt, GAgentApprovalCompletionStatus, AGUIEvent>
{
    public Task EmitAsync(
        GAgentApprovalAcceptedReceipt receipt,
        GAgentApprovalCompletionStatus completion,
        bool completed,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(emitAsync);

        if (!completed || completion != GAgentApprovalCompletionStatus.TextMessageCompleted)
            return Task.CompletedTask;

        return emitAsync(
            new AGUIEvent
            {
                RunFinished = new RunFinishedEvent
                {
                    ThreadId = receipt.ActorId,
                    RunId = receipt.CommandId,
                },
            },
            ct).AsTask();
    }
}

internal sealed class GAgentApprovalDurableCompletionResolver
    : ICommandDurableCompletionResolver<GAgentApprovalAcceptedReceipt, GAgentApprovalCompletionStatus>
{
    private readonly IGAgentRunTerminalQueryPort _queryPort;

    public GAgentApprovalDurableCompletionResolver(
        IGAgentRunTerminalQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public async Task<CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>> ResolveAsync(
        GAgentApprovalAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        try
        {
            var snapshot = await _queryPort.GetByCorrelationIdAsync(receipt.ActorId, receipt.CorrelationId, ct);
            if (!MatchesReceipt(snapshot, receipt))
                snapshot = null;
            if (snapshot == null && !string.IsNullOrWhiteSpace(receipt.SessionId))
            {
                var sessionSnapshot = await _queryPort.GetBySessionIdAsync(receipt.ActorId, receipt.SessionId, ct);
                if (MatchesReceipt(sessionSnapshot, receipt))
                    snapshot = sessionSnapshot;
            }
            return Map(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>.Incomplete;
        }
    }

    private static bool MatchesReceipt(
        GAgentRunTerminalSnapshot? snapshot,
        GAgentApprovalAcceptedReceipt receipt) =>
        snapshot != null &&
        string.Equals(snapshot.ActorId, receipt.ActorId, StringComparison.Ordinal) &&
        string.Equals(snapshot.CorrelationId, receipt.CorrelationId, StringComparison.Ordinal) &&
        snapshot.InteractionKind == GAgentRunTerminalInteractionKind.Approval;

    private static CommandDurableCompletionObservation<GAgentApprovalCompletionStatus> Map(
        GAgentRunTerminalSnapshot? snapshot) =>
        snapshot?.Status switch
        {
            GAgentRunTerminalStatus.TextMessageCompleted => new(true, GAgentApprovalCompletionStatus.TextMessageCompleted),
            GAgentRunTerminalStatus.RunFinished => new(true, GAgentApprovalCompletionStatus.RunFinished),
            GAgentRunTerminalStatus.Failed => new(true, GAgentApprovalCompletionStatus.Failed),
            GAgentRunTerminalStatus.OutcomeUncertain => new(true, GAgentApprovalCompletionStatus.OutcomeUncertain),
            _ => CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>.Incomplete,
        };
}
