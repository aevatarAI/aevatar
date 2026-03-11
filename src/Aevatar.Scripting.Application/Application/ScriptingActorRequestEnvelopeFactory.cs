using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public static class ScriptingActorRequestEnvelopeFactory
{
    private const string RequestPublisherId = "scripting.application";

    public static EventEnvelope Create(
        string targetActorId,
        string correlationId,
        IMessage payload,
        string? publisherId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentNullException.ThrowIfNull(payload);
        var resolvedPublisherId = string.IsNullOrWhiteSpace(publisherId)
            ? RequestPublisherId
            : publisherId;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = new EnvelopeRoute
            {
                PublisherActorId = resolvedPublisherId,
                Direction = EventDirection.Self,
                TargetActorId = targetActorId,
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId ?? string.Empty,
            },
        };
    }
}
