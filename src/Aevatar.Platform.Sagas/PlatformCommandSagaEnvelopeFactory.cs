using Aevatar.Platform.Application.Abstractions.Commands;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Platform.Sagas;

public static class PlatformCommandSagaEnvelopeFactory
{
    public const string SagaActorId = "platform.command.saga";

    public static EventEnvelope Create(
        PlatformCommandStatus status,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(PlatformCommandSagaPayload.Build(status)),
            PublisherId = "platform.command",
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
            TargetActorId = SagaActorId,
        };
        return envelope;
    }
}
