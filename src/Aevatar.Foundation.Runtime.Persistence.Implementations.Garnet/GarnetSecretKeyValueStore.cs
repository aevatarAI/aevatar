using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public sealed class GarnetSecretKeyValueStore : IGarnetSecretKeyValueStore
{
    private const string CompareSetScript = """
        local current = redis.call('GET', KEYS[1])
        if current == false or current ~= ARGV[1] then
            return 0
        end
        redis.call('SET', KEYS[1], ARGV[2])
        return 1
        """;

    private const string CompareDeleteScript = """
        local current = redis.call('GET', KEYS[1])
        if current == false or current ~= ARGV[1] then
            return 0
        end
        redis.call('DEL', KEYS[1])
        return 1
        """;

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

    public async Task<bool> CompareSetAsync(
        string key,
        ReadOnlyMemory<byte> expectedValue,
        ReadOnlyMemory<byte> newValue,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var result = await _database.ScriptEvaluateAsync(
            CompareSetScript,
            [key],
            [expectedValue.ToArray(), newValue.ToArray()]);
        ct.ThrowIfCancellationRequested();

        return (long)result == 1;
    }

    public async Task<bool> CompareDeleteAsync(
        string key,
        ReadOnlyMemory<byte> expectedValue,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var result = await _database.ScriptEvaluateAsync(
            CompareDeleteScript,
            [key],
            [expectedValue.ToArray()]);
        ct.ThrowIfCancellationRequested();

        return (long)result == 1;
    }

    private static Expiration ToExpiration(TimeSpan? expiry) =>
        expiry.HasValue ? expiry.Value : Expiration.Default;
}
