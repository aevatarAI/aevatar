using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

/// <summary>
/// A webhook route binding managed as scope-owned DATA, not host deployment
/// configuration. Static <see cref="WorkflowWebhookIngressOptions.Bindings"/>
/// entries remain supported as an operator fallback, but adding a
/// webhook-driven workflow must not require touching appsettings or
/// redeploying the host: scope members register bindings through the
/// management API and the ingress resolves them dynamically by route key.
/// </summary>
public sealed record WorkflowWebhookBindingRecord(
    string RouteKey,
    string ScopeId,
    string WorkflowName,
    string? SourceId,
    string? PromptTemplate,
    string? PromptJsonPath,
    string? DeliveryIdHeader,
    string? DeliveryIdJsonPath,
    string HmacSecret,
    string? HmacSignatureHeader,
    string? HmacTimestampHeader,
    int MaxTimestampSkewSeconds,
    long UpdatedAtUnixMs,
    string? DefinitionActorId = null,
    string? TargetRevisionId = null,
    string? PreviousHmacSecret = null)
{
    public WorkflowWebhookIngressBindingOptions ToBindingOptions() => new()
    {
        RouteKey = RouteKey,
        SourceId = SourceId,
        WorkflowName = WorkflowName,
        DefinitionActorId = DefinitionActorId,
        ScopeId = ScopeId,
        PromptTemplate = PromptTemplate,
        PromptJsonPath = PromptJsonPath,
        DeliveryIdHeader = DeliveryIdHeader,
        DeliveryIdJsonPath = DeliveryIdJsonPath,
        HmacSecret = HmacSecret,
        PreviousHmacSecret = PreviousHmacSecret,
        HmacSignatureHeader = HmacSignatureHeader,
        HmacTimestampHeader = HmacTimestampHeader,
        MaxTimestampSkewSeconds = MaxTimestampSkewSeconds,
    };
}

public interface IWorkflowWebhookBindingStore
{
    Task<WorkflowWebhookBindingRecord?> GetAsync(string routeKey, CancellationToken ct = default);

    Task PutAsync(WorkflowWebhookBindingRecord record, CancellationToken ct = default);

    Task<bool> DeleteAsync(string routeKey, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowWebhookBindingRecord>> ListByScopeAsync(string scopeId, CancellationToken ct = default);
}

internal sealed class InMemoryWorkflowWebhookBindingStore : IWorkflowWebhookBindingStore
{
    private readonly ConcurrentDictionary<string, WorkflowWebhookBindingRecord> _records =
        new(StringComparer.Ordinal);

    public Task<WorkflowWebhookBindingRecord?> GetAsync(string routeKey, CancellationToken ct = default)
    {
        _records.TryGetValue(routeKey, out var record);
        return Task.FromResult(record);
    }

    public Task PutAsync(WorkflowWebhookBindingRecord record, CancellationToken ct = default)
    {
        _records[record.RouteKey] = record;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string routeKey, CancellationToken ct = default) =>
        Task.FromResult(_records.TryRemove(routeKey, out _));

    public Task<IReadOnlyList<WorkflowWebhookBindingRecord>> ListByScopeAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        IReadOnlyList<WorkflowWebhookBindingRecord> result = _records.Values
            .Where(record => string.Equals(record.ScopeId, scopeId, StringComparison.Ordinal))
            .OrderBy(static record => record.RouteKey, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(result);
    }
}

internal sealed class RedisWorkflowWebhookBindingStore : IWorkflowWebhookBindingStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WorkflowWebhookReplayRedisConnection _connection;
    private readonly IWorkflowWebhookBindingSecretCipher _secretCipher;
    private readonly int _database;
    private readonly string _keyPrefix;

    public RedisWorkflowWebhookBindingStore(
        WorkflowWebhookReplayRedisConnection connection,
        IWorkflowWebhookBindingSecretCipher secretCipher,
        IOptions<WorkflowWebhookIngressOptions> options)
    {
        _connection = connection;
        _secretCipher = secretCipher;
        _database = options.Value.RedisDatabase;
        _keyPrefix = string.IsNullOrWhiteSpace(options.Value.RedisKeyPrefix)
            ? "aevatar:workflow:webhook-replay"
            : options.Value.RedisKeyPrefix.Trim();
    }

    private StackExchange.Redis.IDatabase Database => _connection.GetDatabase(_database);

    public async Task<WorkflowWebhookBindingRecord?> GetAsync(string routeKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var value = await Database.StringGetAsync(BindingKey(routeKey));
        if (value.IsNullOrEmpty)
            return null;

        var record = JsonSerializer.Deserialize<WorkflowWebhookBindingRecord>(value.ToString(), SerializerOptions);
        return record == null
            ? null
            : record with
            {
                HmacSecret = _secretCipher.Unprotect(record.HmacSecret),
                PreviousHmacSecret = record.PreviousHmacSecret == null
                    ? null
                    : _secretCipher.Unprotect(record.PreviousHmacSecret),
            };
    }

    public async Task PutAsync(WorkflowWebhookBindingRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var protectedRecord = record with
        {
            HmacSecret = _secretCipher.Protect(record.HmacSecret),
            PreviousHmacSecret = record.PreviousHmacSecret == null
                ? null
                : _secretCipher.Protect(record.PreviousHmacSecret),
        };
        var payload = JsonSerializer.Serialize(protectedRecord, SerializerOptions);
        var database = Database;
        await database.StringSetAsync(BindingKey(record.RouteKey), payload);
        await database.SetAddAsync(ScopeIndexKey(record.ScopeId), record.RouteKey);
    }

    public async Task<bool> DeleteAsync(string routeKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var existing = await GetAsync(routeKey, ct);
        var database = Database;
        var removed = await database.KeyDeleteAsync(BindingKey(routeKey));
        if (existing != null)
            await database.SetRemoveAsync(ScopeIndexKey(existing.ScopeId), routeKey);
        return removed;
    }

    public async Task<IReadOnlyList<WorkflowWebhookBindingRecord>> ListByScopeAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var members = await Database.SetMembersAsync(ScopeIndexKey(scopeId));
        var records = new List<WorkflowWebhookBindingRecord>(members.Length);
        foreach (var member in members)
        {
            var record = await GetAsync(member.ToString(), ct);
            // Self-heal index entries whose binding was deleted out-of-band.
            if (record == null)
                await Database.SetRemoveAsync(ScopeIndexKey(scopeId), member);
            else if (string.Equals(record.ScopeId, scopeId, StringComparison.Ordinal))
                records.Add(record);
        }

        return records
            .OrderBy(static record => record.RouteKey, StringComparer.Ordinal)
            .ToArray();
    }

    private RedisKey BindingKey(string routeKey) => $"{_keyPrefix}:bindings:{routeKey}";

    private RedisKey ScopeIndexKey(string scopeId) => $"{_keyPrefix}:bindings-by-scope:{scopeId}";
}
