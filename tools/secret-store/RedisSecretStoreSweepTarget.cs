using System.Globalization;
using StackExchange.Redis;

namespace Aevatar.SecretStore.Tools;

public sealed class RedisSecretStoreSweepTarget : ISecretStoreSweepTarget, IDisposable
{
    private const string CompareAndSetScript =
        """
        local current = redis.call('GET', KEYS[1])
        if not current then
          return {-2, -2}
        end
        if current ~= ARGV[1] then
          return {-1, -1}
        end
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl >= 0 then
          redis.call('PSETEX', KEYS[1], ttl, ARGV[2])
        else
          redis.call('SET', KEYS[1], ARGV[2])
        end
        return {1, ttl}
        """;

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

        RedisKey[] keys = [(RedisKey)key];
        RedisValue[] values = [expectedValue, newValue];
        var result = await _database.ScriptEvaluateAsync(CompareAndSetScript, keys, values);
        ct.ThrowIfCancellationRequested();

        var parts = ReadArray(result, "CAS result");
        var status = (long)(RedisValue)parts[0];
        var ttl = (long)(RedisValue)parts[1];
        return status switch
        {
            1 => SecretStoreCasResult.Updated(ttl),
            -2 => SecretStoreCasResult.Missing(),
            _ => SecretStoreCasResult.Conflict(),
        };
    }

    public void Dispose() => _connection.Dispose();

    private static RedisResult[] ReadArray(RedisResult result, string label) =>
        (RedisResult[]?)result ?? throw new InvalidOperationException($"Redis {label} was not an array.");
}
