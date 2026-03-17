namespace Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka;

public sealed class KafkaStreamEnvelopeMessage
{
    public string StreamNamespace { get; set; } = string.Empty;

    public string StreamId { get; set; } = string.Empty;

    public byte[] Payload { get; set; } = [];
}
