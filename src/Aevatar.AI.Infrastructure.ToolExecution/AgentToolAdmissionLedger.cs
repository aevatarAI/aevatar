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
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var added = await _database.StringSetAsync(
            key,
            value.ToArray(),
            expiry: null,
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

internal sealed class DistributedAgentToolAdmissionLedger(
    IAgentToolAdmissionFactStore store) : IAgentToolAdmissionLedger
{
    private const string KeyPrefix = "aevatar:mainnet:agent-tool-admission:v1:";
    private readonly IAgentToolAdmissionFactStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public async Task<AgentToolAdmissionResult> TryStartAsync(
        AgentToolAdmissionFact fact,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var key = BuildKey(fact.AdmissionId);
        var fingerprint = ComputeFingerprint(fact);
        try
        {
            if (await _store.SetIfAbsentAsync(key, fingerprint, ct).ConfigureAwait(false))
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

    internal static string BuildKey(string admissionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(admissionId);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(admissionId));
        return KeyPrefix + Convert.ToHexStringLower(digest);
    }

    internal static byte[] ComputeFingerprint(AgentToolAdmissionFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return SHA256.HashData(fact.ToByteArray());
    }
}

internal sealed class InMemoryAgentToolAdmissionLedger : IAgentToolAdmissionLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _fingerprints = new(StringComparer.Ordinal);

    public Task<AgentToolAdmissionResult> TryStartAsync(
        AgentToolAdmissionFact fact,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ct.ThrowIfCancellationRequested();
        var key = DistributedAgentToolAdmissionLedger.BuildKey(fact.AdmissionId);
        var fingerprint = DistributedAgentToolAdmissionLedger.ComputeFingerprint(fact);
        lock (_gate)
        {
            if (!_fingerprints.TryGetValue(key, out var existing))
            {
                _fingerprints.Add(key, fingerprint);
                return Task.FromResult(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
            }

            return Task.FromResult(new AgentToolAdmissionResult(
                CryptographicOperations.FixedTimeEquals(existing, fingerprint)
                    ? AgentToolAdmissionStatus.Duplicate
                    : AgentToolAdmissionStatus.Conflict));
        }
    }
}
