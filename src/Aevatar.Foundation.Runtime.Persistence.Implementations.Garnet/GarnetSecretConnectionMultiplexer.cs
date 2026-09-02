using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public interface IGarnetSecretConnection
{
    IDatabase GetDatabase(int database);
}

public sealed class GarnetSecretConnectionMultiplexer : IGarnetSecretConnection, IDisposable
{
    private readonly IConnectionMultiplexer _connectionMultiplexer;

    public GarnetSecretConnectionMultiplexer(GarnetSecretStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var connectionOptions = ConfigurationOptions.Parse(options.ConnectionString);
        connectionOptions.AbortOnConnectFail = false;
        _connectionMultiplexer = ConnectionMultiplexer.Connect(connectionOptions);
    }

    public IDatabase GetDatabase(int database) => _connectionMultiplexer.GetDatabase(database);

    public void Dispose()
    {
        _connectionMultiplexer.Dispose();
    }
}
