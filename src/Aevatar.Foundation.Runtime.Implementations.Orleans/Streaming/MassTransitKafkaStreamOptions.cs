namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class MassTransitKafkaStreamOptions
{
    public string StreamNamespace { get; set; } = OrleansRuntimeConstants.ActorEventStreamNamespace;
}
