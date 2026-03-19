namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class AevatarOrleansRuntimeOptions
{
    public const string StreamBackendInMemory = "InMemory";
    public const string StreamBackendKafkaStrictProvider = "KafkaStrictProvider";
    public const string PersistenceBackendInMemory = "InMemory";
    public const string PersistenceBackendGarnet = "Garnet";
    public const string DefaultGarnetConnectionString = "localhost:6379";

    public string StreamBackend { get; set; } = StreamBackendInMemory;

    public string StreamProviderName { get; set; } = OrleansRuntimeConstants.DefaultOrleansStreamProviderName;

    public string ActorEventNamespace { get; set; } = OrleansRuntimeConstants.ActorEventStreamNamespace;

    public string PersistenceBackend { get; set; } = PersistenceBackendInMemory;

    public string GarnetConnectionString { get; set; } = DefaultGarnetConnectionString;

    public int QueueCount { get; set; } = 8;

    public int QueueCacheSize { get; set; } = 4096;
}
