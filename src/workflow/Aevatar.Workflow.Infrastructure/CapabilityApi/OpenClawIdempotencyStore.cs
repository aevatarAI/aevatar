using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Workflow.Application.Abstractions.OpenClaw;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed class ManifestBackedOpenClawIdempotencyStore : IOpenClawIdempotencyStore
{
    private const string ManifestTypeName = "Aevatar.Workflow.OpenClaw.Idempotency";

    private readonly IAgentManifestStore _manifestStore;
    private readonly IProjectionOwnershipCoordinator _ownershipCoordinator;
    private readonly ILogger<ManifestBackedOpenClawIdempotencyStore> _logger;

    public ManifestBackedOpenClawIdempotencyStore(
        IAgentManifestStore manifestStore,
        IProjectionOwnershipCoordinator ownershipCoordinator,
        ILogger<ManifestBackedOpenClawIdempotencyStore> logger)
    {
        _manifestStore = manifestStore;
        _ownershipCoordinator = ownershipCoordinator;
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
        var ownershipScopeId = BuildOwnershipScopeId(idempotencyKey);
        var acquisitionSessionId = Guid.NewGuid().ToString("N");

        try
        {
            await _ownershipCoordinator.AcquireAsync(ownershipScopeId, acquisitionSessionId, ct);
        }
        catch (InvalidOperationException ex)
        {
            if (!IsOwnershipBusy(ex))
                throw;

            _logger.LogDebug(
                ex,
                "OpenClaw idempotency ownership is already active. key={IdempotencyKey}",
                idempotencyKey);

            var existingBusyRecord = await LoadRecordAsync(manifestId, ct);
            if (existingBusyRecord != null && !existingBusyRecord.IsExpired(nowUnixMs))
                return new OpenClawIdempotencyAcquireResult(MapAcquireStatus(existingBusyRecord.Status), existingBusyRecord);

            return new OpenClawIdempotencyAcquireResult(
                OpenClawIdempotencyAcquireStatus.ExistingPending,
                BuildProvisionalPendingRecord(request, idempotencyKey, nowUnixMs, expiresAtUnixMs));
        }

        try
        {
            var existing = await LoadRecordAsync(manifestId, ct);
            if (existing != null && !existing.IsExpired(nowUnixMs))
                return new OpenClawIdempotencyAcquireResult(MapAcquireStatus(existing.Status), existing);

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
            await TryReleaseOwnershipAsync(ownershipScopeId, acquisitionSessionId);
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

    private async Task TryReleaseOwnershipAsync(
        string scopeId,
        string sessionId)
    {
        try
        {
            await _ownershipCoordinator.ReleaseAsync(scopeId, sessionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "OpenClaw idempotency ownership release skipped. scope={ScopeId}",
                scopeId);
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

    private static OpenClawIdempotencyRecord BuildProvisionalPendingRecord(
        OpenClawIdempotencyAcquireRequest request,
        string idempotencyKey,
        long nowUnixMs,
        long expiresAtUnixMs) =>
        new()
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

    private static string BuildOwnershipScopeId(string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        var hash = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"openclaw:idempotency:{hash[..32]}";
    }

    private static bool IsOwnershipBusy(InvalidOperationException ex) =>
        ex.Message.Contains("already active", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
