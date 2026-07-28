using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

public sealed class GarnetSecretKeyValueStore : IGarnetSecretKeyValueStore
{
    private const long MaximumRelativeExpiryMilliseconds = int.MaxValue;
    private const long MaximumRelativeExpiryTicks =
        MaximumRelativeExpiryMilliseconds * TimeSpan.TicksPerMillisecond;

    private const string CompareSetScript = """
        local current = redis.call('GET', KEYS[1])
        if current == false or current ~= ARGV[1] then
            return 0
        end
        local existingTtl = redis.call('PTTL', KEYS[1])
        local requestedTtl = tonumber(ARGV[3])
        local effectiveTtl = requestedTtl
        if existingTtl >= 0 and (requestedTtl == -1 or existingTtl < requestedTtl) then
            effectiveTtl = existingTtl
        end
        local maximumRelativeMilliseconds = tonumber(ARGV[4])
        if effectiveTtl == -1 then
            redis.call('SET', KEYS[1], ARGV[2])
        elseif effectiveTtl > maximumRelativeMilliseconds then
            redis.call('SET', KEYS[1], ARGV[2], 'EX', math.ceil(effectiveTtl / 1000))
        else
            redis.call('PSETEX', KEYS[1], math.max(1, effectiveTtl), ARGV[2])
        end
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

    public async Task<bool> SetIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan? expiry,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var created = await _database.StringSetAsync(
            key,
            value.ToArray(),
            ToExpiration(expiry),
            When.NotExists);
        ct.ThrowIfCancellationRequested();
        return created;
    }

    public async Task<bool> CompareSetAsync(
        string key,
        ReadOnlyMemory<byte> expectedValue,
        ReadOnlyMemory<byte> newValue,
        TimeSpan? expiry,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var result = await _database.ScriptEvaluateAsync(
            CompareSetScript,
            [key],
            [
                expectedValue.ToArray(),
                newValue.ToArray(),
                expiry.HasValue ? ToExpiryMilliseconds(expiry.Value) : -1,
                MaximumRelativeExpiryMilliseconds,
            ]);
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

    private static Expiration ToExpiration(TimeSpan? expiry)
    {
        if (!expiry.HasValue || expiry.Value == TimeSpan.MaxValue)
            return Expiration.Default;

        var ttl = expiry.Value;
        if (ttl.Ticks <= MaximumRelativeExpiryTicks)
            return new Expiration(ttl);

        return new Expiration(TimeSpan.FromSeconds(ToGarnetCompatibleWholeSeconds(ttl)));
    }

    private static long ToGarnetCompatibleWholeSeconds(TimeSpan ttl)
    {
        var wholeSeconds = ttl.Ticks / TimeSpan.TicksPerSecond;
        if (ttl.Ticks % TimeSpan.TicksPerSecond != 0)
            wholeSeconds = checked(wholeSeconds + 1);
        if (wholeSeconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                "Expiry exceeds Garnet's supported whole-second range.");
        }
        return wholeSeconds;
    }

    private static long ToExpiryMilliseconds(TimeSpan expiry)
    {
        if (expiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Expiry must be positive.");
        if (expiry.Ticks > MaximumRelativeExpiryTicks)
            _ = ToGarnetCompatibleWholeSeconds(expiry);

        var wholeMilliseconds = expiry.Ticks / TimeSpan.TicksPerMillisecond;
        if (expiry.Ticks % TimeSpan.TicksPerMillisecond != 0)
            wholeMilliseconds = checked(wholeMilliseconds + 1);

        return wholeMilliseconds;
    }
}
