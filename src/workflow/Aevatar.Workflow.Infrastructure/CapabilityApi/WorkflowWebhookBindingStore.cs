using System.Collections.Concurrent;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
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
    string? PreviousHmacSecret = null,
    string? TimeZoneId = null,
    string? CallerBearerToken = null)
{
    public WorkflowWebhookIngressBindingOptions ToBindingOptions() => new()
    {
        RouteKey = RouteKey,
        SourceId = SourceId,
        WorkflowName = WorkflowName,
        DefinitionActorId = DefinitionActorId,
        TargetRevisionId = TargetRevisionId,
        ScopeId = ScopeId,
        PromptTemplate = PromptTemplate,
        PromptJsonPath = PromptJsonPath,
        TimeZoneId = TimeZoneId,
        DeliveryIdHeader = DeliveryIdHeader,
        DeliveryIdJsonPath = DeliveryIdJsonPath,
        HmacSecret = HmacSecret,
        PreviousHmacSecret = PreviousHmacSecret,
        CallerBearerToken = CallerBearerToken,
        HmacSignatureHeader = HmacSignatureHeader,
        HmacTimestampHeader = HmacTimestampHeader,
        MaxTimestampSkewSeconds = MaxTimestampSkewSeconds,
    };
}

public interface IWorkflowWebhookBindingStore
{
    Task<WorkflowWebhookBindingRecord?> GetAsync(string routeKey, CancellationToken ct = default);

    /// <summary>
    /// Atomically creates a route or updates it when it is already owned by
    /// the same scope. Returns false when another scope owns the route.
    /// </summary>
    Task<bool> TryPutOwnedAsync(WorkflowWebhookBindingRecord record, CancellationToken ct = default);

    /// <summary>
    /// Atomically removes a route only while it is still owned by the
    /// expected scope. A concurrent delete-and-rebind can never remove the
    /// replacement owner's binding.
    /// </summary>
    Task<bool> TryDeleteOwnedAsync(
        string routeKey,
        string scopeId,
        CancellationToken ct = default);

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

    public Task<bool> TryPutOwnedAsync(WorkflowWebhookBindingRecord record, CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_records.TryGetValue(record.RouteKey, out var existing))
            {
                if (!string.Equals(existing.ScopeId, record.ScopeId, StringComparison.Ordinal))
                    return Task.FromResult(false);

                if (_records.TryUpdate(record.RouteKey, record, existing))
                    return Task.FromResult(true);
                continue;
            }

            if (_records.TryAdd(record.RouteKey, record))
                return Task.FromResult(true);
        }
    }

    public Task<bool> TryDeleteOwnedAsync(
        string routeKey,
        string scopeId,
        CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!_records.TryGetValue(routeKey, out var existing) ||
                !string.Equals(existing.ScopeId, scopeId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            var removed = ((ICollection<KeyValuePair<string, WorkflowWebhookBindingRecord>>)_records)
                .Remove(new KeyValuePair<string, WorkflowWebhookBindingRecord>(routeKey, existing));
            if (removed)
                return Task.FromResult(true);
        }
    }

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

        return FromState(WorkflowWebhookBindingState.Parser.ParseFrom((byte[])value!));
    }

    public async Task<bool> TryPutOwnedAsync(
        WorkflowWebhookBindingRecord record,
        CancellationToken ct = default)
    {
        var payload = ToState(record).ToByteArray();
        var bindingKey = BindingKey(record.RouteKey);
        var database = Database;

        // Optimistic Redis transaction closes the check-then-write ownership
        // race without requiring payload parsing support in the server's Lua
        // implementation. Same-scope concurrent updates retry against the
        // latest value; another scope can never replace the route.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var existingValue = await database.StringGetAsync(bindingKey);
            if (!existingValue.IsNullOrEmpty)
            {
                var existing = WorkflowWebhookBindingState.Parser.ParseFrom((byte[])existingValue!);
                if (!string.Equals(existing.ScopeId, record.ScopeId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            var transaction = database.CreateTransaction();
            transaction.AddCondition(existingValue.IsNullOrEmpty
                ? Condition.KeyNotExists(bindingKey)
                : Condition.StringEqual(bindingKey, existingValue));
            _ = transaction.StringSetAsync(bindingKey, payload);
            _ = transaction.SetAddAsync(ScopeIndexKey(record.ScopeId), record.RouteKey);
            if (await transaction.ExecuteAsync())
                return true;
        }

        throw new InvalidOperationException(
            "Workflow webhook binding could not be updated because the route changed concurrently.");
    }

    public async Task<bool> TryDeleteOwnedAsync(
        string routeKey,
        string scopeId,
        CancellationToken ct = default)
    {
        var bindingKey = BindingKey(routeKey);
        var database = Database;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var existingValue = await database.StringGetAsync(bindingKey);
            if (existingValue.IsNullOrEmpty)
                return false;

            var existing = WorkflowWebhookBindingState.Parser.ParseFrom((byte[])existingValue!);
            if (!string.Equals(existing.ScopeId, scopeId, StringComparison.Ordinal))
                return false;

            var transaction = database.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(bindingKey, existingValue));
            _ = transaction.KeyDeleteAsync(bindingKey);
            _ = transaction.SetRemoveAsync(ScopeIndexKey(scopeId), routeKey);
            if (await transaction.ExecuteAsync())
                return true;
        }

        throw new InvalidOperationException(
            "Workflow webhook binding could not be deleted because the route changed concurrently.");
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

    private WorkflowWebhookBindingState ToState(WorkflowWebhookBindingRecord record) => new()
    {
        RouteKey = record.RouteKey,
        ScopeId = record.ScopeId,
        WorkflowName = record.WorkflowName,
        SourceId = record.SourceId ?? string.Empty,
        PromptTemplate = record.PromptTemplate ?? string.Empty,
        PromptJsonPath = record.PromptJsonPath ?? string.Empty,
        DeliveryIdHeader = record.DeliveryIdHeader ?? string.Empty,
        DeliveryIdJsonPath = record.DeliveryIdJsonPath ?? string.Empty,
        ProtectedHmacSecret = _secretCipher.Protect(record.HmacSecret),
        HmacSignatureHeader = record.HmacSignatureHeader ?? string.Empty,
        HmacTimestampHeader = record.HmacTimestampHeader ?? string.Empty,
        MaxTimestampSkewSeconds = record.MaxTimestampSkewSeconds,
        UpdatedAtUnixMs = record.UpdatedAtUnixMs,
        DefinitionActorId = record.DefinitionActorId ?? string.Empty,
        TargetRevisionId = record.TargetRevisionId ?? string.Empty,
        ProtectedPreviousHmacSecret = record.PreviousHmacSecret == null
            ? string.Empty
            : _secretCipher.Protect(record.PreviousHmacSecret),
        TimeZoneId = record.TimeZoneId ?? string.Empty,
        ProtectedCallerBearerToken = record.CallerBearerToken == null
            ? string.Empty
            : _secretCipher.Protect(record.CallerBearerToken),
    };

    private WorkflowWebhookBindingRecord FromState(WorkflowWebhookBindingState state) => new(
        RouteKey: state.RouteKey,
        ScopeId: state.ScopeId,
        WorkflowName: state.WorkflowName,
        SourceId: NullIfEmpty(state.SourceId),
        PromptTemplate: NullIfEmpty(state.PromptTemplate),
        PromptJsonPath: NullIfEmpty(state.PromptJsonPath),
        DeliveryIdHeader: NullIfEmpty(state.DeliveryIdHeader),
        DeliveryIdJsonPath: NullIfEmpty(state.DeliveryIdJsonPath),
        HmacSecret: _secretCipher.Unprotect(state.ProtectedHmacSecret),
        HmacSignatureHeader: NullIfEmpty(state.HmacSignatureHeader),
        HmacTimestampHeader: NullIfEmpty(state.HmacTimestampHeader),
        MaxTimestampSkewSeconds: state.MaxTimestampSkewSeconds,
        UpdatedAtUnixMs: state.UpdatedAtUnixMs,
        DefinitionActorId: NullIfEmpty(state.DefinitionActorId),
        TargetRevisionId: NullIfEmpty(state.TargetRevisionId),
        PreviousHmacSecret: state.ProtectedPreviousHmacSecret.Length == 0
            ? null
            : _secretCipher.Unprotect(state.ProtectedPreviousHmacSecret),
        TimeZoneId: NullIfEmpty(state.TimeZoneId),
        CallerBearerToken: state.ProtectedCallerBearerToken.Length == 0
            ? null
            : _secretCipher.Unprotect(state.ProtectedCallerBearerToken));

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
