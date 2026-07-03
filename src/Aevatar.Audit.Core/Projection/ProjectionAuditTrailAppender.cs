using System.Security.Cryptography;
using Aevatar.Audit.Abstractions.Ports;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Audit.Core.Projection;

public sealed class ProjectionAuditTrailAppender : IAuditTrailAppender
{
    private readonly IAuditTrailArtifactStore? _store;
    private readonly ILogger<ProjectionAuditTrailAppender> _logger;

    public ProjectionAuditTrailAppender(
        IEnumerable<IAuditTrailArtifactStore> stores,
        ILogger<ProjectionAuditTrailAppender>? logger = null)
    {
        _store = SelectSingleOrDefault(stores, nameof(stores));
        _logger = logger ?? NullLogger<ProjectionAuditTrailAppender>.Instance;
    }

    public async Task<AuditTrailAppendResult> AppendAsync(Audit.AuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var auditId = record.AuditId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(auditId))
            return AuditTrailAppendResult.Conflict(string.Empty, "Audit id is required.");

        if (_store is null)
            return AuditTrailAppendResult.StoreUnavailable(auditId, "Audit trail artifact store is not registered.");

        try
        {
            var contentHash = ComputeContentHash(record);
            var existing = await _store.GetAsync(auditId, ct);
            if (existing != null)
            {
                return string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal)
                    ? AuditTrailAppendResult.Duplicate(auditId)
                    : AuditTrailAppendResult.Conflict(auditId, "Audit id already exists with different content.");
            }

            var observedAt = record.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
            var document = AuditTrailDocumentFactory.Create(record, auditId, contentHash, observedAt);
            var write = await _store.UpsertAsync(document, ct);
            return ToAppendResult(write, auditId, record.AuditActorId ?? string.Empty, observedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit trail append failed. auditId={AuditId}", auditId);
            return AuditTrailAppendResult.StoreUnavailable(auditId, ex.Message);
        }
    }

    private static string ComputeContentHash(Audit.AuditRecord record)
    {
        var bytes = record.ToByteArray();
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static AuditTrailAppendResult ToAppendResult(
        AuditTrailArtifactWriteResult write,
        string auditId,
        string auditActorId,
        DateTimeOffset observedAt) =>
        write.Disposition switch
        {
            AuditTrailArtifactWriteDisposition.Applied => AuditTrailAppendResult.Appended(
                auditId,
                auditActorId,
                observedAt),
            AuditTrailArtifactWriteDisposition.Duplicate => AuditTrailAppendResult.Duplicate(auditId),
            AuditTrailArtifactWriteDisposition.Conflict => AuditTrailAppendResult.Conflict(auditId, "Audit artifact write conflict."),
            _ => AuditTrailAppendResult.StoreUnavailable(auditId, $"Audit artifact write was not applied: {write.Disposition}."),
        };

    private static T? SelectSingleOrDefault<T>(IEnumerable<T> registrations, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(registrations, parameterName);
        using var enumerator = registrations.GetEnumerator();
        if (!enumerator.MoveNext())
            return default;

        var selected = enumerator.Current;
        if (enumerator.MoveNext())
            throw new InvalidOperationException($"Multiple audit trail artifact store registrations were found for {typeof(T).Name}.");

        return selected;
    }
}
