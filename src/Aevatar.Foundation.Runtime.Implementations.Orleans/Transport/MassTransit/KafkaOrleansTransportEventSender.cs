using MassTransit;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

public sealed class KafkaOrleansTransportEventSender(
    ITopicProducer<OrleansTransportEventMessage> producer) : IOrleansTransportEventSender
{
    public Task SendAsync(string targetActorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentNullException.ThrowIfNull(envelope);

        var message = new OrleansTransportEventMessage
        {
            TargetActorId = targetActorId,
            EnvelopeBytes = envelope.ToByteArray(),
        };
        return producer.Produce(message, ct);
    }
}
