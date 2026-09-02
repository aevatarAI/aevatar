using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf;
using StackExchange.Redis;

namespace Aevatar.AI.Infrastructure.ToolExecution;

internal interface IAgentToolAdmissionFactStore
{
    Task<bool> SetIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan retention,
        CancellationToken ct = default);

    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);
}

internal sealed class GarnetAgentToolAdmissionFactStore : IAgentToolAdmissionFactStore
{
    private readonly IDatabase _database;

    public GarnetAgentToolAdmissionFactStore(IConnectionMultiplexer connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _database = connection.GetDatabase();
    }

    public async Task<bool> SetIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan retention,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention));
        var added = await _database.StringSetAsync(
            key,
            value.ToArray(),
            expiry: retention,
            When.NotExists).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return added;
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var value = await _database.StringGetAsync(key).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return value.IsNull ? null : (byte[]?)value;
    }
}

internal sealed class DistributedAgentToolAdmissionLedger : IAgentToolAdmissionLedger
{
    private readonly IAgentToolAdmissionFactStore _store;
    private readonly AgentToolAdmissionLedgerOptions _ledgerOptions;
    private readonly AgentToolAdmissionPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public DistributedAgentToolAdmissionLedger(
        IAgentToolAdmissionFactStore store,
        AgentToolAdmissionLedgerOptions ledgerOptions)
        : this(store, ledgerOptions, AgentToolAdmissionPolicy.Default, null)
    {
    }

    public DistributedAgentToolAdmissionLedger(
        IAgentToolAdmissionFactStore store,
        AgentToolAdmissionLedgerOptions ledgerOptions,
        AgentToolAdmissionPolicy policy,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ledgerOptions = ledgerOptions ?? throw new ArgumentNullException(nameof(ledgerOptions));
        _ledgerOptions.Validate();
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _policy.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AgentToolAdmissionResult> TryStartAsync(
        AgentToolAdmissionFact fact,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var lifetime = EvaluateLifetime(fact, _policy, _timeProvider.GetUtcNow());
        if (lifetime.Rejection is not null)
            return lifetime.Rejection;

        var key = BuildKey(_ledgerOptions.KeyPrefix, fact.AdmissionId);
        var fingerprint = ComputeFingerprint(fact);
        try
        {
            if (await _store.SetIfAbsentAsync(
                    key,
                    fingerprint,
                    lifetime.Retention,
                    ct).ConfigureAwait(false))
                return new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started);

            var existing = await _store.GetAsync(key, ct).ConfigureAwait(false);
            if (existing is null)
            {
                return new AgentToolAdmissionResult(
                    AgentToolAdmissionStatus.StoreUnavailable,
                    "The admission fact could not be read after the atomic insert was rejected.");
            }

            return new AgentToolAdmissionResult(
                CryptographicOperations.FixedTimeEquals(existing, fingerprint)
                    ? AgentToolAdmissionStatus.Duplicate
                    : AgentToolAdmissionStatus.Conflict);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolAdmissionResult(
                AgentToolAdmissionStatus.StoreUnavailable,
                ex.GetType().Name);
        }
    }

    internal static string BuildKey(string keyPrefix, string admissionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(admissionId);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(admissionId));
        return keyPrefix + Convert.ToHexStringLower(digest);
    }

    internal static byte[] ComputeFingerprint(AgentToolAdmissionFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return SHA256.HashData(fact.ToByteArray());
    }

    internal static AgentToolAdmissionLifetime EvaluateLifetime(
        AgentToolAdmissionFact fact,
        AgentToolAdmissionPolicy policy,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(fact.OperationId))
        {
            return AgentToolAdmissionLifetime.Reject(
                AgentToolAdmissionStatus.InvalidFact,
                "The tool admission fact is missing its operation identity.");
        }

        if (fact.ReplayPolicy == AgentToolReplayPolicy.Unspecified ||
            !Enum.IsDefined(fact.ReplayPolicy))
        {
            return AgentToolAdmissionLifetime.Reject(
                AgentToolAdmissionStatus.InvalidFact,
                "The tool admission fact is missing a supported replay policy.");
        }

        if (fact.IssuedAtUnixMs <= 0)
        {
            return AgentToolAdmissionLifetime.Reject(
                AgentToolAdmissionStatus.InvalidFact,
                "The tool admission fact is missing its issued time.");
        }

        DateTimeOffset issuedAt;
        try
        {
            issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(fact.IssuedAtUnixMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return AgentToolAdmissionLifetime.Reject(
                AgentToolAdmissionStatus.InvalidFact,
                "The tool admission fact has an invalid issued time.");
        }

        if (issuedAt > now + policy.MaximumFutureClockSkew)
        {
            return AgentToolAdmissionLifetime.Reject(
                AgentToolAdmissionStatus.InvalidFact,
                "The tool admission fact is outside the allowed future clock skew.");
        }

        var expiresAt = issuedAt + policy.MaximumReplayWindow;
        if (expiresAt <= now)
        {
            return AgentToolAdmissionLifetime.Reject(
                AgentToolAdmissionStatus.Expired,
                "The tool admission fact is outside the configured replay window.");
        }

        return new AgentToolAdmissionLifetime(expiresAt - now, null);
    }
}

internal readonly record struct AgentToolAdmissionLifetime(
    TimeSpan Retention,
    AgentToolAdmissionResult? Rejection)
{
    public static AgentToolAdmissionLifetime Reject(
        AgentToolAdmissionStatus status,
        string safeMessage) =>
        new(TimeSpan.Zero, new AgentToolAdmissionResult(status, safeMessage));
}

internal sealed class InMemoryAgentToolAdmissionLedger : IAgentToolAdmissionLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly AgentToolAdmissionPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public InMemoryAgentToolAdmissionLedger()
        : this(AgentToolAdmissionPolicy.Default, null)
    {
    }

    public InMemoryAgentToolAdmissionLedger(
        AgentToolAdmissionPolicy policy,
        TimeProvider? timeProvider = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _policy.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<AgentToolAdmissionResult> TryStartAsync(
        AgentToolAdmissionFact fact,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ct.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var lifetime = DistributedAgentToolAdmissionLedger.EvaluateLifetime(fact, _policy, now);
        if (lifetime.Rejection is not null)
            return Task.FromResult(lifetime.Rejection);

        var key = DistributedAgentToolAdmissionLedger.BuildKey("in-memory:", fact.AdmissionId);
        var fingerprint = DistributedAgentToolAdmissionLedger.ComputeFingerprint(fact);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing) && existing.ExpiresAt <= now)
            {
                _entries.Remove(key);
                existing = null;
            }

            if (existing is null)
            {
                _entries.Add(key, new Entry(fingerprint, now + lifetime.Retention));
                return Task.FromResult(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
            }

            return Task.FromResult(new AgentToolAdmissionResult(
                CryptographicOperations.FixedTimeEquals(existing.Fingerprint, fingerprint)
                    ? AgentToolAdmissionStatus.Duplicate
                    : AgentToolAdmissionStatus.Conflict));
        }
    }

    private sealed record Entry(byte[] Fingerprint, DateTimeOffset ExpiresAt);
}
