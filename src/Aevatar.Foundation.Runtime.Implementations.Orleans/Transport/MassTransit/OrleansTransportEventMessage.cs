namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit;

[GenerateSerializer]
public sealed class OrleansTransportEventMessage
{
    [Id(0)]
    public string TargetActorId { get; set; } = string.Empty;

    [Id(1)]
    public byte[] EnvelopeBytes { get; set; } = [];
}
