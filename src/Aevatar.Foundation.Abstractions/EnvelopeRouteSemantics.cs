namespace Aevatar.Foundation.Abstractions;

public static class EnvelopeRouteSemantics
{
    public static EnvelopeRoute CreateTopologyPublication(string publisherActorId, TopologyAudience audience) =>
        new()
        {
            PublisherActorId = publisherActorId ?? string.Empty,
            Publication = new PublicationRoute
            {
                Topology = new TopologyPublication
                {
                    Audience = audience,
                },
            },
        };

    public static EnvelopeRoute CreateDirect(string publisherActorId, string targetActorId) =>
        new()
        {
            PublisherActorId = publisherActorId ?? string.Empty,
            Direct = new DirectRoute
            {
                TargetActorId = targetActorId ?? string.Empty,
            },
        };

    public static EnvelopeRoute CreateObserverPublication(
        string publisherActorId,
        ObserverAudience audience = ObserverAudience.CommittedFacts) =>
        new()
        {
            PublisherActorId = publisherActorId ?? string.Empty,
            Publication = new PublicationRoute
            {
                Observer = new ObserverPublication
                {
                    Audience = audience,
                },
            },
        };

    public static TopologyAudience GetTopologyAudience(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Publication &&
        route.Publication?.AudienceCase == PublicationRoute.AudienceOneofCase.Topology
            ? route.Publication.Topology.Audience
            : TopologyAudience.Unspecified;

    public static ObserverAudience GetObserverAudience(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Publication &&
        route.Publication?.AudienceCase == PublicationRoute.AudienceOneofCase.Observer
            ? route.Publication.Observer.Audience
            : ObserverAudience.Unspecified;

    public static string GetTargetActorId(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Direct
            ? route.Direct.TargetActorId ?? string.Empty
            : string.Empty;

    public static bool IsPublication(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Publication;

    public static bool IsTopologyPublication(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Publication &&
        route.Publication?.AudienceCase == PublicationRoute.AudienceOneofCase.Topology;

    public static bool IsDirect(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Direct;

    public static bool IsObserverPublication(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Publication &&
        route.Publication?.AudienceCase == PublicationRoute.AudienceOneofCase.Observer;

    public static string Describe(this EnvelopeRoute? route) =>
        route?.RouteCase switch
        {
            EnvelopeRoute.RouteOneofCase.Direct => nameof(DirectRoute),
            EnvelopeRoute.RouteOneofCase.Publication => route.Publication?.AudienceCase switch
            {
                PublicationRoute.AudienceOneofCase.Topology => route.Publication.Topology.Audience.ToString(),
                PublicationRoute.AudienceOneofCase.Observer => route.Publication.Observer.Audience.ToString(),
                _ => nameof(PublicationRoute),
            },
            _ => TopologyAudience.Unspecified.ToString(),
        };
}
