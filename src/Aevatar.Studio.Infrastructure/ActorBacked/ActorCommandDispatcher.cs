using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Shared utility for dispatching command events to actors via <see cref="IActor.HandleEventAsync"/>.
/// Eliminates duplicated <c>SendCommandAsync</c> across all ActorBacked stores.
/// </summary>
internal static class ActorCommandDispatcher
{
    public static async Task SendAsync(IActor actor, IMessage command, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };
        await actor.HandleEventAsync(envelope, ct);
    }
}
