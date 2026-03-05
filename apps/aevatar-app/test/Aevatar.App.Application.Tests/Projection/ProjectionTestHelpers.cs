using Aevatar.App.Application.Projection;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.App.Application.Tests.Projection;

internal static class ProjectionTestHelpers
{
    internal static EventEnvelope PackEnvelope<TEvent>(TEvent evt, string? eventId = null, DateTime? timestamp = null)
        where TEvent : IMessage
    {
        var ts = timestamp ?? DateTime.UtcNow;
        return new EventEnvelope
        {
            Id = eventId ?? Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(ts),
            Payload = Any.Pack(evt),
            PublisherId = "test",
            Direction = EventDirection.Self,
        };
    }

    internal static AppProjectionContext CreateContext(string actorId) => new()
    {
        ActorId = actorId,
        RootActorId = actorId,
    };
}
