namespace Aevatar.CQRS.Projection.Core.Orchestration;

public static class ProjectionDispatchRouteFilter
{
    public static bool ShouldDispatch(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Route == null || envelope.Route.RouteCase == EnvelopeRoute.RouteOneofCase.None)
            return true;

        return envelope.Route.IsTopologyPublication() || envelope.Route.IsObserverPublication();
    }
}
