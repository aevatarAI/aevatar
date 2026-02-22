namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

public sealed class MassTransitEnvelopeRecord
{
    public string StreamNamespace { get; set; } = string.Empty;

    public string StreamId { get; set; } = string.Empty;

    public byte[] Payload { get; set; } = [];
}
