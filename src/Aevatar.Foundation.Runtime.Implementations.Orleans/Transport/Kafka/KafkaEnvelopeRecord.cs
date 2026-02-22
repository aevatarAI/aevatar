namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;

public sealed class KafkaEnvelopeRecord
{
    public string StreamNamespace { get; set; } = string.Empty;

    public string StreamId { get; set; } = string.Empty;

    public byte[] Payload { get; set; } = [];
}
