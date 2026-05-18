using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Demos.Inspector.ReadModels;

internal static class InspectorActorCommandDispatcher
{
    private const string PublisherActorId = "aevatar.demos.inspector";

    public static Task SendAsync<TEvent>(
        IActorDispatchPort dispatchPort,
        IActor actor,
        TEvent evt,
        CancellationToken ct = default)
        where TEvent : IMessage
    {
        ArgumentNullException.ThrowIfNull(dispatchPort);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(evt);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actor.Id),
        };

        return dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }
}
