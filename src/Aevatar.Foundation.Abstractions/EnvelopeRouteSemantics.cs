namespace Aevatar.Foundation.Abstractions;

public static class EnvelopeRouteSemantics
{
    public static EnvelopeRoute CreateBroadcast(string publisherActorId, BroadcastDirection direction) =>
        new()
        {
            PublisherActorId = publisherActorId ?? string.Empty,
            Broadcast = new BroadcastRoute
            {
                Direction = direction,
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

    public static EnvelopeRoute CreateObserve(string publisherActorId) =>
        new()
        {
            PublisherActorId = publisherActorId ?? string.Empty,
            Observe = new ObserveRoute(),
        };

    public static BroadcastDirection GetBroadcastDirection(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Broadcast
            ? route.Broadcast.Direction
            : BroadcastDirection.Unspecified;

    public static string GetTargetActorId(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Direct
            ? route.Direct.TargetActorId ?? string.Empty
            : string.Empty;

    public static bool IsBroadcast(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Broadcast;

    public static bool IsDirect(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Direct;

    public static bool IsObserve(this EnvelopeRoute? route) =>
        route?.RouteCase == EnvelopeRoute.RouteOneofCase.Observe;

    public static string Describe(this EnvelopeRoute? route) =>
        route?.RouteCase switch
        {
            EnvelopeRoute.RouteOneofCase.Broadcast => route.Broadcast.Direction.ToString(),
            EnvelopeRoute.RouteOneofCase.Direct => nameof(DirectRoute),
            EnvelopeRoute.RouteOneofCase.Observe => nameof(ObserveRoute),
            _ => BroadcastDirection.Unspecified.ToString(),
        };
}
