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

    public GAgentApprovalCommandTarget(
        IActor actor,
        IGAgentDraftRunProjectionPort projectionPort)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public IActor Actor { get; }
    public string TargetId => Actor.Id;
    public string ActorId => Actor.Id;
    public IGAgentDraftRunProjectionLease? ProjectionLease { get; private set; }
    public IEventSink<AGUIEvent>? LiveSink { get; private set; }

    public void BindLiveObservation(
        IGAgentDraftRunProjectionLease lease,
        IEventSink<AGUIEvent> sink)
    {
        ProjectionLease = lease ?? throw new ArgumentNullException(nameof(lease));
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
                    sink,
                    null,
                    ct);
                ProjectionLease = null;
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
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
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

    public GAgentApprovalCommandTargetResolver(
        IActorRuntime actorRuntime,
        IGAgentDraftRunProjectionPort projectionPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
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
            new GAgentApprovalCommandTarget(actor, _projectionPort));
    }
}

internal sealed class GAgentApprovalCommandTargetBinder
    : ICommandTargetBinder<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalStartError>
{
    private readonly IGAgentDraftRunProjectionPort _projectionPort;

    public GAgentApprovalCommandTargetBinder(
        IGAgentDraftRunProjectionPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<CommandTargetBindingResult<GAgentApprovalStartError>> BindAsync(
        GAgentApprovalCommand command,
        GAgentApprovalCommandTarget target,
        CommandContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var sink = new EventChannel<AGUIEvent>();

        try
        {
            var projectionLease = await _projectionPort.EnsureAndAttachAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    target.ActorId,
                    context.CommandId,
                    token),
                sink,
                ct);

            if (projectionLease == null)
            {
                sink.Complete();
                await sink.DisposeAsync();
                throw new InvalidOperationException("GAgent approval projection pipeline is unavailable.");
            }

            target.BindLiveObservation(projectionLease, sink);
            return CommandTargetBindingResult<GAgentApprovalStartError>.Success();
        }
        catch
        {
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
            context.CorrelationId);
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
    public Task<CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>> ResolveAsync(
        GAgentApprovalAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        _ = ct;
        return Task.FromResult(CommandDurableCompletionObservation<GAgentApprovalCompletionStatus>.Incomplete);
    }
}
