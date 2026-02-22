namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class AevatarOrleansRuntimeOptions
{
    public const string StreamBackendInMemory = "InMemory";
    public const string StreamBackendMassTransitAdapter = "MassTransitAdapter";

    public string StreamBackend { get; set; } = StreamBackendInMemory;

    public string StreamProviderName { get; set; } = OrleansRuntimeConstants.DefaultOrleansStreamProviderName;

    public string ActorEventNamespace { get; set; } = OrleansRuntimeConstants.ActorEventStreamNamespace;

    public int QueueCount { get; set; } = 8;

    public int QueueCacheSize { get; set; } = 4096;
}
