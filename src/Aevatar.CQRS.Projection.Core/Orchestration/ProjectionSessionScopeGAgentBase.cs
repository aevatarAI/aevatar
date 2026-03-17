using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public abstract class ProjectionSessionScopeGAgentBase<TContext>
    : ProjectionScopeGAgentBase<TContext>
    where TContext : class, IProjectionSessionContext
{
    protected override ProjectionRuntimeMode RuntimeMode =>
        ProjectionRuntimeMode.SessionObservation;

    protected override async ValueTask<ProjectionScopeDispatchResult> ProcessObservationCoreAsync(
        TContext context,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!ProjectionDispatchRouteFilter.ShouldDispatch(envelope))
            return ProjectionScopeDispatchResult.Skip(envelope.Payload?.TypeUrl ?? string.Empty);

        var observedVersion = CommittedStateEventEnvelope.TryGetObservedPayload(
            envelope,
            out var payload,
            out _,
            out var stateVersion)
            ? stateVersion
            : 0;
        var eventType = payload?.TypeUrl ?? envelope.Payload?.TypeUrl ?? string.Empty;

        try
        {
            await ProjectionScopeDispatchExecutor.ExecuteProjectorsAsync(
                ResolveProjectors(),
                context,
                envelope,
                ct);
            return ProjectionScopeDispatchResult.Success(observedVersion, observedVersion, eventType);
        }
        catch (Exception ex)
        {
            await RecordDispatchFailureAsync(
                "projection-execution",
                envelope.Id ?? string.Empty,
                eventType,
                observedVersion,
                ex.Message ?? ex.GetType().Name,
                envelope);
            throw;
        }
    }

    private IEnumerable<IProjectionProjector<TContext>> ResolveProjectors() =>
        Services.GetServices<IProjectionProjector<TContext>>();
}
