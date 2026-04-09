using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Sends a domain event (command) to a target actor by wrapping it in an
/// <see cref="EventEnvelope"/> directed to the actor's own inbox.
/// </summary>
internal static class ActorCommandDispatcher
{
    public static Task SendAsync<TEvent>(
        IActor actor, TEvent evt, CancellationToken ct = default)
        where TEvent : IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                actor.Id, TopologyAudience.Self),
        };

        return actor.HandleEventAsync(envelope, ct);
    }
}
