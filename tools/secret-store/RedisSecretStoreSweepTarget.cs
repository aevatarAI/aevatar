using System.Globalization;
using StackExchange.Redis;

namespace Aevatar.SecretStore.Tools;

public sealed class RedisSecretStoreSweepTarget : ISecretStoreSweepTarget, IDisposable
{
    private readonly ConnectionMultiplexer _connection;
    private readonly IDatabase _database;

    private RedisSecretStoreSweepTarget(ConnectionMultiplexer connection, int database)
    {
        _connection = connection;
        _database = connection.GetDatabase(database);
    }

    public static async Task<RedisSecretStoreSweepTarget> ConnectAsync(string connectionString, int database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        var connection = await ConnectionMultiplexer.ConnectAsync(options);
        return new RedisSecretStoreSweepTarget(connection, database);
    }

    public async Task<SecretStoreScanBatch> ScanAsync(
        string pattern,
        long cursor,
        int count,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (cursor < 0)
            throw new ArgumentOutOfRangeException(nameof(cursor));
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        ct.ThrowIfCancellationRequested();

        // IDatabase.ExecuteAsync routes through the selected database; this is required so
        // --database N scans the same logical DB as Get/CAS (raw IServer.Execute does not).
        var result = await _database.ExecuteAsync(
            "SCAN",
            cursor.ToString(CultureInfo.InvariantCulture),
            "MATCH",
            pattern,
            "COUNT",
            count.ToString(CultureInfo.InvariantCulture));
        ct.ThrowIfCancellationRequested();

        var parts = ReadArray(result, "SCAN result");
        var nextCursor = long.Parse(((RedisValue)parts[0]).ToString(), CultureInfo.InvariantCulture);
        var keyResults = ReadArray(parts[1], "SCAN keys");
        var keys = keyResults
            .Select(item => ((RedisValue)item).ToString())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToArray();

        return new SecretStoreScanBatch(nextCursor, keys);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();
        var value = await _database.StringGetAsync(key);
        ct.ThrowIfCancellationRequested();
        return value.IsNull ? null : (byte[]?)value;
    }

    public async Task<SecretStoreCasResult> CompareExchangeAsync(
        string key,
        byte[] expectedValue,
        byte[] newValue,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(expectedValue);
        ArgumentNullException.ThrowIfNull(newValue);
        ct.ThrowIfCancellationRequested();

        var currentValue = await _database.StringGetAsync(key);
        ct.ThrowIfCancellationRequested();
        if (currentValue.IsNull)
            return SecretStoreCasResult.Missing();
        if (!ValueEquals(currentValue, expectedValue))
            return SecretStoreCasResult.Conflict();

        var ttl = await _database.KeyTimeToLiveAsync(key);
        ct.ThrowIfCancellationRequested();

        var transaction = _database.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual((RedisKey)key, (RedisValue)expectedValue));
        var setTask = transaction.StringSetAsync((RedisKey)key, (RedisValue)newValue, ToExpiration(ttl));
        var executed = await transaction.ExecuteAsync();
        ct.ThrowIfCancellationRequested();

        if (!executed || !await setTask)
            return await ClassifyFailedCompareExchangeAsync(key, ct);

        return SecretStoreCasResult.Updated(PreservedTtlMs(ttl));
    }

    public void Dispose() => _connection.Dispose();

    private async Task<SecretStoreCasResult> ClassifyFailedCompareExchangeAsync(string key, CancellationToken ct)
    {
        var currentValue = await _database.StringGetAsync(key);
        ct.ThrowIfCancellationRequested();
        return currentValue.IsNull
            ? SecretStoreCasResult.Missing()
            : SecretStoreCasResult.Conflict();
    }

    private static bool ValueEquals(RedisValue value, byte[] expectedValue) =>
        ((byte[]?)value)?.SequenceEqual(expectedValue) == true;

    private static long PreservedTtlMs(TimeSpan? ttl) =>
        ttl.HasValue
            ? Math.Max(0, (long)Math.Ceiling(ttl.Value.TotalMilliseconds))
            : -1;

    private static Expiration ToExpiration(TimeSpan? ttl) =>
        ttl.HasValue ? ttl.Value : Expiration.Default;

    private static RedisResult[] ReadArray(RedisResult result, string label) =>
        (RedisResult[]?)result ?? throw new InvalidOperationException($"Redis {label} was not an array.");
}
