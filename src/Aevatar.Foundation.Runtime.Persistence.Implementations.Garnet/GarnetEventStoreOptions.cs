namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

/// <summary>
/// Configuration for Garnet-backed event-store persistence.
/// </summary>
public sealed class GarnetEventStoreOptions
{
    /// <summary>
    /// Garnet connection string (Redis protocol), for example: localhost:6379,abortConnect=false.
    /// </summary>
    public string ConnectionString { get; set; } = "localhost:6379,abortConnect=false";

    /// <summary>
    /// Logical key prefix for event streams.
    /// </summary>
    public string KeyPrefix { get; set; } = "aevatar:eventstore";

    /// <summary>
    /// Database index. -1 uses the default database from connection options.
    /// </summary>
    public int Database { get; set; } = -1;
}
