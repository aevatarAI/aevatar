using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Persistence;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class OpenClawIdempotencyStatuses
{
    public const string Pending = "pending";
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

internal enum OpenClawIdempotencyAcquireStatus
{
    Acquired = 0,
    ExistingPending = 1,
    ExistingStarted = 2,
    ExistingCompleted = 3,
    ExistingFailed = 4,
}

internal sealed record OpenClawIdempotencyAcquireRequest(
    string IdempotencyKey,
    string SessionKey,
    string CorrelationId,
    string ActorId,
    string WorkflowName,
    string ChannelId,
    string UserId,
    string MessageId,
    int TtlHours);

internal sealed record OpenClawIdempotencyAcquireResult(
    OpenClawIdempotencyAcquireStatus Status,
    OpenClawIdempotencyRecord? Record);

internal sealed record OpenClawIdempotencyRecord
{
    public required string IdempotencyKey { get; init; }
    public required string SessionKey { get; init; }
    public required string CorrelationId { get; init; }
    public required string ActorId { get; init; }
    public required string WorkflowName { get; init; }
    public required string ChannelId { get; init; }
    public required string UserId { get; init; }
    public required string MessageId { get; init; }
    public required string Status { get; init; }
    public required long CreatedAtUnixMs { get; init; }
    public required long UpdatedAtUnixMs { get; init; }
    public required long ExpiresAtUnixMs { get; init; }
    public string CommandId { get; init; } = "";
    public string LastErrorCode { get; init; } = "";
    public string LastErrorMessage { get; init; } = "";

    public bool IsExpired(long nowUnixMs) =>
        ExpiresAtUnixMs > 0 && ExpiresAtUnixMs <= nowUnixMs;
}

internal interface IOpenClawIdempotencyStore
{
    Task<OpenClawIdempotencyAcquireResult> AcquireAsync(
        OpenClawIdempotencyAcquireRequest request,
        CancellationToken ct = default);

    Task MarkStartedAsync(
        string idempotencyKey,
        string actorId,
        string commandId,
        string workflowName,
        CancellationToken ct = default);

    Task MarkCompletedAsync(
        string idempotencyKey,
        bool success,
        string errorCode,
        string errorMessage,
        CancellationToken ct = default);
}

internal sealed class ManifestBackedOpenClawIdempotencyStore : IOpenClawIdempotencyStore
{
    private const string ManifestTypeName = "Aevatar.Workflow.OpenClaw.Idempotency";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IAgentManifestStore _manifestStore;
    private readonly ILogger<ManifestBackedOpenClawIdempotencyStore> _logger;

    public ManifestBackedOpenClawIdempotencyStore(
        IAgentManifestStore manifestStore,
        ILogger<ManifestBackedOpenClawIdempotencyStore> logger)
    {
        _manifestStore = manifestStore;
        _logger = logger;
    }

    public async Task<OpenClawIdempotencyAcquireResult> AcquireAsync(
        OpenClawIdempotencyAcquireRequest request,
        CancellationToken ct = default)
    {
        var idempotencyKey = NormalizeToken(request.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return new OpenClawIdempotencyAcquireResult(OpenClawIdempotencyAcquireStatus.Acquired, null);

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresAtUnixMs = DateTimeOffset.UtcNow
            .AddHours(Math.Clamp(request.TtlHours, 1, 24 * 30))
            .ToUnixTimeMilliseconds();
        var manifestId = BuildManifestId(idempotencyKey);

        await Gate.WaitAsync(ct);
        try
        {
            var existing = await LoadRecordAsync(manifestId, ct);
            if (existing != null && !existing.IsExpired(nowUnixMs))
            {
                return new OpenClawIdempotencyAcquireResult(MapAcquireStatus(existing.Status), existing);
            }

            var pending = new OpenClawIdempotencyRecord
            {
                IdempotencyKey = idempotencyKey,
                SessionKey = NormalizeToken(request.SessionKey),
                CorrelationId = NormalizeToken(request.CorrelationId),
                ActorId = NormalizeToken(request.ActorId),
                WorkflowName = NormalizeToken(request.WorkflowName),
                ChannelId = NormalizeToken(request.ChannelId),
                UserId = NormalizeToken(request.UserId),
                MessageId = NormalizeToken(request.MessageId),
                Status = OpenClawIdempotencyStatuses.Pending,
                CreatedAtUnixMs = nowUnixMs,
                UpdatedAtUnixMs = nowUnixMs,
                ExpiresAtUnixMs = expiresAtUnixMs,
            };

            await SaveRecordAsync(manifestId, pending, ct);
            return new OpenClawIdempotencyAcquireResult(OpenClawIdempotencyAcquireStatus.Acquired, pending);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task MarkStartedAsync(
        string idempotencyKey,
        string actorId,
        string commandId,
        string workflowName,
        CancellationToken ct = default)
    {
        var normalizedIdempotencyKey = NormalizeToken(idempotencyKey);
        if (string.IsNullOrWhiteSpace(normalizedIdempotencyKey))
            return;

        var manifestId = BuildManifestId(normalizedIdempotencyKey);
        await Gate.WaitAsync(ct);
        try
        {
            var current = await LoadRecordAsync(manifestId, ct);
            if (current == null)
                return;

            var updated = current with
            {
                ActorId = NormalizeToken(actorId),
                CommandId = NormalizeToken(commandId),
                WorkflowName = NormalizeToken(workflowName),
                Status = OpenClawIdempotencyStatuses.Started,
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            await SaveRecordAsync(manifestId, updated, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task MarkCompletedAsync(
        string idempotencyKey,
        bool success,
        string errorCode,
        string errorMessage,
        CancellationToken ct = default)
    {
        var normalizedIdempotencyKey = NormalizeToken(idempotencyKey);
        if (string.IsNullOrWhiteSpace(normalizedIdempotencyKey))
            return;

        var manifestId = BuildManifestId(normalizedIdempotencyKey);
        await Gate.WaitAsync(ct);
        try
        {
            var current = await LoadRecordAsync(manifestId, ct);
            if (current == null)
                return;

            var status = success
                ? OpenClawIdempotencyStatuses.Completed
                : OpenClawIdempotencyStatuses.Failed;
            var updated = current with
            {
                Status = status,
                LastErrorCode = NormalizeToken(errorCode),
                LastErrorMessage = NormalizeToken(errorMessage),
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            await SaveRecordAsync(manifestId, updated, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<OpenClawIdempotencyRecord?> LoadRecordAsync(string manifestId, CancellationToken ct)
    {
        var manifest = await _manifestStore.LoadAsync(manifestId, ct);
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.ConfigJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<OpenClawIdempotencyRecord>(manifest.ConfigJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to deserialize OpenClaw idempotency record. manifestId={ManifestId}",
                manifestId);
            return null;
        }
    }

    private Task SaveRecordAsync(
        string manifestId,
        OpenClawIdempotencyRecord record,
        CancellationToken ct)
    {
        var manifest = new AgentManifest
        {
            AgentId = manifestId,
            AgentTypeName = ManifestTypeName,
            ConfigJson = JsonSerializer.Serialize(record),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["openclaw.idempotency_key"] = record.IdempotencyKey,
                ["openclaw.status"] = record.Status,
                ["openclaw.actor_id"] = record.ActorId,
                ["openclaw.command_id"] = record.CommandId,
                ["openclaw.updated_at"] = record.UpdatedAtUnixMs.ToString(),
            },
        };
        return _manifestStore.SaveAsync(manifestId, manifest, ct);
    }

    private static OpenClawIdempotencyAcquireStatus MapAcquireStatus(string status) =>
        status switch
        {
            OpenClawIdempotencyStatuses.Pending => OpenClawIdempotencyAcquireStatus.ExistingPending,
            OpenClawIdempotencyStatuses.Started => OpenClawIdempotencyAcquireStatus.ExistingStarted,
            OpenClawIdempotencyStatuses.Completed => OpenClawIdempotencyAcquireStatus.ExistingCompleted,
            OpenClawIdempotencyStatuses.Failed => OpenClawIdempotencyAcquireStatus.ExistingFailed,
            _ => OpenClawIdempotencyAcquireStatus.ExistingStarted,
        };

    private static string BuildManifestId(string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        var hash = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"openclaw.idempotency.{hash[..32]}";
    }

    private static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
