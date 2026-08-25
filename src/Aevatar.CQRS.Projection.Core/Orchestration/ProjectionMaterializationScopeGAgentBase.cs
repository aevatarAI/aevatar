using Microsoft.Extensions.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public abstract class ProjectionMaterializationScopeGAgentBase<TContext>
    : ProjectionScopeGAgentBase<TContext>
    where TContext : class, IProjectionMaterializationContext
{
    protected override ProjectionRuntimeMode RuntimeMode =>
        ProjectionRuntimeMode.DurableMaterialization;

    protected override async ValueTask<ProjectionScopeDispatchResult> ProcessObservationCoreAsync(
        TContext context,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!ProjectionDispatchRouteFilter.ShouldDispatch(envelope))
            return ProjectionScopeDispatchResult.Skip(envelope.Payload?.TypeUrl ?? string.Empty);

        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) == true &&
            !CommittedStateEventEnvelope.TryUnpack(envelope, out _))
        {
            await RecordDispatchFailureAsync(
                "payload-normalization",
                envelope.Id ?? string.Empty,
                envelope.Payload?.TypeUrl ?? string.Empty,
                0,
                "Committed observation payload is invalid.",
                envelope);
            return ProjectionScopeDispatchResult.Skip(envelope.Payload?.TypeUrl ?? string.Empty);
        }

        if (!CommittedStateEventEnvelope.TryUnpack(envelope, out var published) || published?.StateEvent == null)
            return ProjectionScopeDispatchResult.Skip(envelope.Payload?.TypeUrl ?? string.Empty);

        var stateEvent = published.StateEvent;
        var observedVersion = stateEvent.Version;
        var eventType = stateEvent.EventData?.TypeUrl ?? string.Empty;

        try
        {
            await ProjectionScopeDispatchExecutor.ExecuteMaterializersAsync(
                ResolveMaterializers(),
                context,
                envelope,
                ct);
            return ProjectionScopeDispatchResult.Success(observedVersion, eventType);
        }
        catch (Exception ex)
        {
            // The hook may durably change scope state (e.g. roll back a materialization
            // route) and update the context accordingly; a single in-turn retry then
            // converges on the new durable state. No recovery keeps record-and-rethrow.
            if (await TryRecoverObservationAsync(context, envelope, stateEvent, ex, ct))
            {
                await ProjectionScopeDispatchExecutor.ExecuteMaterializersAsync(
                    ResolveMaterializers(),
                    context,
                    envelope,
                    ct);
                return ProjectionScopeDispatchResult.Success(observedVersion, eventType);
            }

            await RecordDispatchFailureAsync(
                "projection-execution",
                stateEvent.EventId ?? envelope.Id ?? string.Empty,
                eventType,
                observedVersion,
                ex.Message ?? ex.GetType().Name,
                envelope);
            throw;
        }
    }

    /// <summary>
    /// Last chance to recover a materialization failure inside the observation turn before it
    /// is recorded as a dispatch failure. Returning true retries the materializers once on the
    /// current context; implementations must first commit the durable state change the retry
    /// converges on and reflect it in the context. Returning false keeps the standard
    /// record-and-rethrow path.
    /// </summary>
    protected virtual ValueTask<bool> TryRecoverObservationAsync(
        TContext context,
        EventEnvelope envelope,
        StateEvent stateEvent,
        Exception error,
        CancellationToken ct) =>
        ValueTask.FromResult(false);

    protected IEnumerable<IProjectionMaterializer<TContext>> ResolveMaterializers() =>
        Services.GetServices<IProjectionMaterializer<TContext>>();
}
