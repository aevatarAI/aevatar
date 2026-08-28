using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;

/// <summary>
/// Garnet-backed event store with optimistic concurrency and stream compaction support.
/// </summary>
public sealed class GarnetEventStore : IEventStore, IEventStoreMaintenance
{
    private const int PayloadReadBatchSize = 64;

    private const string AppendScript = """
                                      local currentRaw = redis.call('GET', KEYS[1])
                                      local current = 0
                                      if currentRaw then
                                        current = tonumber(currentRaw)
                                      end

                                      local expected = tonumber(ARGV[1])
                                      if current ~= expected then
                                        return {0, current}
                                      end

                                      local count = tonumber(ARGV[2])
                                      if count <= 0 then
                                        return {1, current}
                                      end

                                      local stale = redis.call('ZRANGEBYSCORE', KEYS[2], '(' .. tostring(current), '+inf')
                                      if #stale > 0 then
                                        for i = 1, #stale do
                                          redis.call('HDEL', KEYS[3], stale[i])
                                        end
                                        redis.call('ZREMRANGEBYSCORE', KEYS[2], '(' .. tostring(current), '+inf')
                                      end

                                      local latest = current
                                      for i = 0, count - 1 do
                                        local base = 3 + (i * 2)
                                        local version = tonumber(ARGV[base])
                                        local payload = ARGV[base + 1]
                                        local versionField = tostring(version)

                                        redis.call('ZADD', KEYS[2], version, versionField)
                                        redis.call('HSET', KEYS[3], versionField, payload)
                                        latest = version
                                      end

                                      redis.call('SET', KEYS[1], tostring(latest))
                                      return {1, latest}
                                      """;

    private const string DeleteScript = """
                                      local toVersion = tonumber(ARGV[1])
                                      if toVersion <= 0 then
                                        return 0
                                      end

                                      local versions = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', toVersion)
                                      local removed = #versions
                                      if removed == 0 then
                                        return 0
                                      end

                                      redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', toVersion)
                                      for i = 1, removed do
                                        redis.call('HDEL', KEYS[2], versions[i])
                                      end

                                      return removed
                                      """;

    private readonly IDatabase _database;
    private readonly string _keyPrefix;
    private readonly ILogger<GarnetEventStore> _logger;

    public GarnetEventStore(
        IConnectionMultiplexer connectionMultiplexer,
        GarnetEventStoreOptions options,
        ILogger<GarnetEventStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("GarnetEventStore requires a non-empty connection string.");
        if (string.IsNullOrWhiteSpace(options.KeyPrefix))
            throw new InvalidOperationException("GarnetEventStore requires a non-empty key prefix.");

        _database = connectionMultiplexer.GetDatabase(options.Database);
        _keyPrefix = options.KeyPrefix.Trim();
        _logger = logger ?? NullLogger<GarnetEventStore>.Instance;
    }

    public async Task<EventStoreCommitResult> AppendAsync(
        string agentId,
        IEnumerable<StateEvent> events,
        long expectedVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();

        var pendingEvents = events.Select(static evt => evt.Clone()).ToList();
        if (pendingEvents.Count == 0)
        {
            return new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = await GetVersionAsync(agentId, ct),
            };
        }

        ValidateEventVersions(pendingEvents, expectedVersion);

        var keys = BuildKeys(agentId);
        var scriptArgs = BuildAppendScriptArgs(expectedVersion, pendingEvents);
        // Cancellation owns admission only. Once the Lua script is submitted, Garnet's
        // atomic result is authoritative and must not be hidden by a late deadline.
        ct.ThrowIfCancellationRequested();
        var rawResult = await _database.ScriptEvaluateAsync(
            AppendScript,
            [keys.VersionKey, keys.EventIndexKey, keys.EventDataKey],
            scriptArgs);

        var result = (RedisResult[])rawResult!;
        if (result.Length != 2)
            throw new InvalidOperationException("Unexpected Garnet append script result.");

        var status = (long)result[0];
        var actualVersion = (long)result[1];
        if (status == 0)
        {
            throw new EventStoreOptimisticConcurrencyException(
                agentId,
                expectedVersion,
                actualVersion);
        }

        if (status != 1)
            throw new InvalidOperationException($"Unexpected Garnet append script status: {status}.");

        _logger.LogDebug(
            "Garnet event-store append completed. agentId={AgentId} appended={Count} version={Version}",
            agentId,
            pendingEvents.Count,
            actualVersion);
        return new EventStoreCommitResult
        {
            AgentId = agentId,
            LatestVersion = actualVersion,
            CommittedEvents = { pendingEvents.Select(static evt => evt.Clone()) },
        };
    }

    public async Task<IReadOnlyList<StateEvent>> GetEventsAsync(
        string agentId,
        long? fromVersion = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        var keys = BuildKeys(agentId);
        // The version key is the commit point: index/data entries above it are
        // remnants of an append the store aborted mid-script (e.g. ENOSPC) and
        // were never committed. Reading them would replay uncommitted — and
        // possibly payload-less — facts, so bound the range at the committed
        // version instead of +inf.
        var committedVersion = await GetVersionAsync(agentId, ct);
        if (committedVersion <= 0 || (fromVersion.HasValue && fromVersion.Value >= committedVersion))
            return [];

        var versions = await _database.SortedSetRangeByScoreAsync(
            keys.EventIndexKey,
            start: fromVersion ?? double.NegativeInfinity,
            stop: committedVersion,
            exclude: fromVersion.HasValue ? Exclude.Start : Exclude.None,
            order: Order.Ascending);
        ct.ThrowIfCancellationRequested();
        if (versions.Length == 0)
            return [];

        var payloads = await ReadPayloadsAsync(keys.EventDataKey, versions, ct);
        ct.ThrowIfCancellationRequested();
        if (payloads.Length != versions.Length)
        {
            throw new InvalidOperationException(
                $"Corrupted Garnet event stream for agent '{agentId}': version/payload length mismatch.");
        }

        var events = new List<StateEvent>(payloads.Length);
        for (var i = 0; i < payloads.Length; i++)
        {
            var payload = payloads[i];
            if (payload.IsNull)
            {
                throw new InvalidOperationException(
                    $"Corrupted Garnet event stream for agent '{agentId}': missing payload at version '{versions[i]}'.");
            }

            var bytes = (byte[]?)payload;
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Corrupted Garnet event stream for agent '{agentId}': empty payload at version '{versions[i]}'.");
            }

            events.Add(StateEvent.Parser.ParseFrom(bytes));
        }

        return events;
    }

    private async Task<RedisValue[]> ReadPayloadsAsync(
        RedisKey eventDataKey,
        RedisValue[] versions,
        CancellationToken ct)
    {
        var payloads = new RedisValue[versions.Length];
        for (var offset = 0; offset < versions.Length; offset += PayloadReadBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(PayloadReadBatchSize, versions.Length - offset);
            var pendingReads = new Task<RedisValue>[count];
            for (var i = 0; i < count; i++)
            {
                pendingReads[i] = _database.HashGetAsync(eventDataKey, versions[offset + i]);
            }

            // Garnet 1.1.x can terminate the Redis session while materializing a large
            // multi-field HMGET response. Keep pipelining bounded, but use the stable
            // single-field HGET path so one large actor history cannot reset the shared
            // multiplexer and prevent every persisted actor from recovering.
            var batch = await Task.WhenAll(pendingReads);
            batch.CopyTo(payloads, offset);
        }

        return payloads;
    }

    public async Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        var keys = BuildKeys(agentId);
        var rawVersion = await _database.StringGetAsync(keys.VersionKey);
        ct.ThrowIfCancellationRequested();
        if (rawVersion.IsNullOrEmpty)
            return 0;

        if (!long.TryParse(rawVersion.ToString(), out var version))
            throw new InvalidOperationException($"Corrupted Garnet stream version for agent '{agentId}'.");

        return version;
    }

    public async Task<long> DeleteEventsUpToAsync(
        string agentId,
        long toVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();
        if (toVersion <= 0)
            return 0;

        var keys = BuildKeys(agentId);
        var rawDeleted = await _database.ScriptEvaluateAsync(
            DeleteScript,
            [keys.EventIndexKey, keys.EventDataKey],
            [toVersion]);
        ct.ThrowIfCancellationRequested();

        var deleted = (long)rawDeleted;
        _logger.LogDebug(
            "Garnet event-store compaction completed. agentId={AgentId} compactToVersion={CompactToVersion} deletedEvents={DeletedEvents}",
            agentId,
            toVersion,
            deleted);
        return deleted;
    }

    public async Task<bool> ResetStreamAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        var keys = BuildKeys(agentId);
        var deleted = await _database.KeyDeleteAsync(
            [keys.VersionKey, keys.EventIndexKey, keys.EventDataKey]);
        ct.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "Garnet event-store stream reset completed. agentId={AgentId} deletedKeys={DeletedKeys}",
            agentId,
            deleted);
        return deleted > 0;
    }

    private StreamKeys BuildKeys(string agentId)
    {
        var encodedAgentId = EncodeAgentId(agentId);
        return new StreamKeys(
            VersionKey: $"{_keyPrefix}:{{{encodedAgentId}}}:version",
            EventIndexKey: $"{_keyPrefix}:{{{encodedAgentId}}}:index",
            EventDataKey: $"{_keyPrefix}:{{{encodedAgentId}}}:data");
    }

    private static string EncodeAgentId(string agentId)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(agentId))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static RedisValue[] BuildAppendScriptArgs(
        long expectedVersion,
        IReadOnlyList<StateEvent> pendingEvents)
    {
        var values = new RedisValue[2 + (pendingEvents.Count * 2)];
        values[0] = expectedVersion;
        values[1] = pendingEvents.Count;
        for (var i = 0; i < pendingEvents.Count; i++)
        {
            values[2 + (i * 2)] = pendingEvents[i].Version;
            values[3 + (i * 2)] = pendingEvents[i].ToByteArray();
        }

        return values;
    }

    private static void ValidateEventVersions(
        IReadOnlyList<StateEvent> pendingEvents,
        long expectedVersion)
    {
        for (var i = 0; i < pendingEvents.Count; i++)
        {
            var expectedEventVersion = expectedVersion + i + 1;
            if (pendingEvents[i].Version != expectedEventVersion)
            {
                throw new InvalidOperationException(
                    "StateEvent versions must be strictly contiguous and start from expectedVersion + 1.");
            }
        }
    }

    private sealed record StreamKeys(
        RedisKey VersionKey,
        RedisKey EventIndexKey,
        RedisKey EventDataKey);
}
