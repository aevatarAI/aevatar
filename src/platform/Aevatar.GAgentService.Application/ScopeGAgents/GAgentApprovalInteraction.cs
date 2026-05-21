using System.Runtime.ExceptionServices;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Presentation.AGUI;
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
    public string SessionId { get; private set; } = string.Empty;
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
        IEventSink<AGUIEvent> sink,
        string sessionId)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: DefaultCommandDispatchPipeline.PrepareAsync 内 attach projection/session binder(混 read-side 关注到 pre-dispatch command 准备)
        //   New principle: 新 CQRS Core ObservationLifecycle port/phase:streaming observation attachment 移到 post-accepted dispatch 之后或独立 lifecycle;PrepareAsync 不再持有 projection/session 关注
        ProjectionLease = lease ?? throw new ArgumentNullException(nameof(lease));
        LiveSinkLease = liveSinkLease;
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
        SessionId = sessionId;
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

internal sealed class GAgentApprovalCommandTargetBinder
    : ICommandTargetBinder<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalStartError>
{
    private readonly IGAgentDraftRunProjectionPort _projectionPort;
    private readonly IGAgentRunTerminalProjectionPort _terminalProjectionPort;

    public GAgentApprovalCommandTargetBinder(
        IGAgentDraftRunProjectionPort projectionPort,
        IGAgentRunTerminalProjectionPort terminalProjectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _terminalProjectionPort = terminalProjectionPort ?? throw new ArgumentNullException(nameof(terminalProjectionPort));
    }

    public async Task<CommandTargetBindingResult<GAgentApprovalStartError>> BindAsync(
        GAgentApprovalCommand command,
        GAgentApprovalCommandTarget target,
        CommandContext context,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: DefaultCommandDispatchPipeline.PrepareAsync 内 attach projection/session binder(混 read-side 关注到 pre-dispatch command 准备)
        //   New principle: 新 CQRS Core ObservationLifecycle port/phase:streaming observation attachment 移到 post-accepted dispatch 之后或独立 lifecycle;PrepareAsync 不再持有 projection/session 关注
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var sink = new EventChannel<AGUIEvent>();
        IGAgentRunTerminalProjectionLease? terminalProjectionLease = null;

        try
        {
            terminalProjectionLease = await _terminalProjectionPort.EnsureProjectionAsync(
                target.ActorId,
                context.CorrelationId,
                GAgentRunTerminalInteractionKind.Approval,
                ct);
            target.BindTerminalProjection(terminalProjectionLease);

            var attachment = await _projectionPort.EnsureAndAttachLeaseAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    target.ActorId,
                    context.CorrelationId,
                    token),
                sink,
                ct);

            if (attachment == null)
            {
                sink.Complete();
                await sink.DisposeAsync();
                throw new InvalidOperationException("GAgent approval projection pipeline is unavailable.");
            }

            target.BindLiveObservation(
                attachment.ProjectionLease,
                attachment.LiveSinkLease,
                sink,
                command.SessionId?.Trim() ?? string.Empty);
            return CommandTargetBindingResult<GAgentApprovalStartError>.Success();
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
}

internal sealed class GAgentApprovalCommandEnvelopeFactory
    : ICommandEnvelopeFactory<GAgentApprovalCommand>
{
    public EventEnvelope CreateEnvelope(GAgentApprovalCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var decisionEvent = new ToolApprovalDecisionEvent
        {
            RequestId = command.RequestId,
            SessionId = command.SessionId?.Trim() ?? string.Empty,
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
            target.SessionId);
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
                completion = GAgentApprovalCompletionStatus.Failed;
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
            _ => CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>.Incomplete,
        };
}
