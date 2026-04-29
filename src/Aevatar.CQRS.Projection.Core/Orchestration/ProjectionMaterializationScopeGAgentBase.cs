using Microsoft.Extensions.DependencyInjection;

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
            await ProjectionScopeDispatchExecutor.ExecuteContinuationsAsync(
                ResolveContinuations(),
                context,
                envelope,
                ct);
            return ProjectionScopeDispatchResult.Success(observedVersion, eventType);
        }
        catch (Exception ex)
        {
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

    private IEnumerable<IProjectionMaterializer<TContext>> ResolveMaterializers() =>
        Services.GetServices<IProjectionMaterializer<TContext>>();

    private IEnumerable<ICommittedObservationContinuation<TContext>> ResolveContinuations() =>
        Services.GetServices<ICommittedObservationContinuation<TContext>>();
}
