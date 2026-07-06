using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public sealed class GarnetSecretKeyValueStore : IGarnetSecretKeyValueStore
{
    private readonly IDatabase _database;

    public GarnetSecretKeyValueStore(IGarnetSecretConnection connection, GarnetSecretStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _database = connection.GetDatabase(options.Database);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var value = await _database.StringGetAsync(key);
        ct.ThrowIfCancellationRequested();

        return value.IsNull ? null : (byte[]?)value;
    }

    public async Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? expiry, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        await _database.StringSetAsync(key, value.ToArray(), ToExpiration(expiry));
        ct.ThrowIfCancellationRequested();
    }

    private static Expiration ToExpiration(TimeSpan? expiry) =>
        expiry.HasValue ? expiry.Value : Expiration.Default;
}
