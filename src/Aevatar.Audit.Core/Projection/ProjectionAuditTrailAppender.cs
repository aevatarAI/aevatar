using System.Security.Cryptography;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Audit.Core.Projection;

public sealed class ProjectionAuditTrailAppender : IAuditTrailAppender
{
    private readonly IProjectionDocumentReader<Audit.AuditTrailDocument, string>? _reader;
    private readonly IProjectionDocumentWriter<Audit.AuditTrailDocument>? _writer;
    private readonly ILogger<ProjectionAuditTrailAppender> _logger;

    public ProjectionAuditTrailAppender(
        IEnumerable<IProjectionDocumentReader<Audit.AuditTrailDocument, string>> readers,
        IEnumerable<IProjectionDocumentWriter<Audit.AuditTrailDocument>> writers,
        ILogger<ProjectionAuditTrailAppender>? logger = null)
    {
        _reader = SelectSingleOrDefault(readers, nameof(readers));
        _writer = SelectSingleOrDefault(writers, nameof(writers));
        _logger = logger ?? NullLogger<ProjectionAuditTrailAppender>.Instance;
    }

    public async Task<AuditTrailAppendResult> AppendAsync(Audit.AuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var auditId = record.AuditId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(auditId))
            return AuditTrailAppendResult.Conflict(string.Empty, "Audit id is required.");

        if (_reader is null || _writer is null)
            return AuditTrailAppendResult.StoreUnavailable(auditId, "Audit trail projection document store is not registered.");

        try
        {
            var contentHash = ComputeContentHash(record);
            var existing = await _reader.GetAsync(auditId, ct);
            if (existing != null)
            {
                return string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal)
                    ? AuditTrailAppendResult.Duplicate(auditId)
                    : AuditTrailAppendResult.Conflict(auditId, "Audit id already exists with different content.");
            }

            var observedAt = record.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
            var document = new Audit.AuditTrailDocument
            {
                Id = auditId,
                AuditId = auditId,
                ContentHash = contentHash,
                Record = record.Clone(),
                OccurredAt = Timestamp.FromDateTimeOffset(observedAt),
                UpdatedAt = Timestamp.FromDateTimeOffset(observedAt),
                AuditActorId = record.AuditActorId ?? string.Empty,
                ScopeId = record.ScopeId ?? string.Empty,
                OperationName = record.OperationName ?? string.Empty,
                Outcome = record.Outcome,
                SensitivityLevel = record.SensitivityLevel,
                TargetKind = record.Target?.Kind ?? string.Empty,
                TargetId = record.Target?.Id ?? string.Empty,
                RequestId = record.Correlation?.RequestId ?? string.Empty,
                CommandId = record.Correlation?.CommandId ?? string.Empty,
                CorrelationId = record.Correlation?.TraceId ?? string.Empty,
                SessionId = record.Correlation?.SessionId ?? string.Empty,
                WorkflowRunId = record.Correlation?.WorkflowRunId ?? string.Empty,
                CommittedEventId = record.CommittedFactRef?.CommittedEventId ?? string.Empty,
                CommittedActorId = record.CommittedFactRef?.ActorId ?? string.Empty,
                CommittedActorType = record.CommittedFactRef?.ActorType ?? string.Empty,
                CommittedEventTypeUrl = record.CommittedFactRef?.EventTypeUrl ?? string.Empty,
                CommittedStateVersion = record.CommittedFactRef?.StateVersion ?? 0,
            };

            var write = await _writer.UpsertAsync(document, ct);
            return write.Disposition switch
            {
                ProjectionWriteDisposition.Applied => AuditTrailAppendResult.Appended(auditId),
                ProjectionWriteDisposition.Duplicate => AuditTrailAppendResult.Duplicate(auditId),
                ProjectionWriteDisposition.Conflict => AuditTrailAppendResult.Conflict(auditId, "Audit document write conflict."),
                _ => AuditTrailAppendResult.StoreUnavailable(auditId, $"Audit document write was not applied: {write.Disposition}."),
            };
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

    private static T? SelectSingleOrDefault<T>(IEnumerable<T> registrations, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(registrations, parameterName);
        using var enumerator = registrations.GetEnumerator();
        if (!enumerator.MoveNext())
            return default;

        var selected = enumerator.Current;
        if (enumerator.MoveNext())
            throw new InvalidOperationException($"Multiple audit trail projection document store registrations were found for {typeof(T).Name}.");

        return selected;
    }
}
