namespace Aevatar.AI.Projection.Appliers;

internal static class AIProjectionApplierHelpers
{
    public static string ResolvePublisher(EventEnvelope envelope) =>
        string.IsNullOrWhiteSpace(envelope.Route?.PublisherActorId) ? "(unknown)" : envelope.Route.PublisherActorId;

    public static string ResolveEventType(EventEnvelope envelope) =>
        envelope.Payload?.TypeUrl ?? string.Empty;
}
