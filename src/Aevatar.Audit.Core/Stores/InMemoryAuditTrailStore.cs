using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.Projection;
using Aevatar.Audit.Core.Sanitization;
using Google.Protobuf;

namespace Aevatar.Audit.Core.Stores;

public sealed class InMemoryAuditTrailStore : IAuditTrailAppender, IAuditTrailQueryPort, IAuditTrailArtifactStore
{
    private const int DefaultTake = 100;
    private const int MaxTake = 500;

    private readonly AuditRecordSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;
    private readonly List<AuditRecord> _records = [];
    private readonly List<AuditTrailDocument> _documents = [];

    public InMemoryAuditTrailStore(
        AuditRecordSanitizer? sanitizer = null,
        TimeProvider? timeProvider = null)
    {
        _sanitizer = sanitizer ?? new AuditRecordSanitizer();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AuditTrailAppendResult> AppendAsync(
        AuditRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sanitized = _sanitizer.Sanitize(record);
        var document = ToDocument(sanitized);
        var write = await UpsertAsync(document, cancellationToken);
        return write.Disposition switch
        {
            AuditTrailArtifactWriteDisposition.Applied => AuditTrailAppendResult.Appended(
                sanitized.AuditId,
                sanitized.AuditActorId,
                sanitized.OccurredAt.ToDateTimeOffset()),
            AuditTrailArtifactWriteDisposition.Duplicate => AuditTrailAppendResult.Duplicate(sanitized.AuditId),
            AuditTrailArtifactWriteDisposition.Conflict => AuditTrailAppendResult.Conflict(
                sanitized.AuditId,
                "Audit id already exists with different content."),
            _ => AuditTrailAppendResult.StoreUnavailable(
                sanitized.AuditId,
                $"Audit artifact write was not applied: {write.Disposition}."),
        };
    }

    public async Task<IReadOnlyList<AuditTrailAppendResult>> AppendManyAsync(
        IReadOnlyList<AuditRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var results = new List<AuditTrailAppendResult>(records.Count);
        foreach (var record in records)
        {
            results.Add(await AppendAsync(record, cancellationToken));
        }

        return results;
    }

    public Task<AuditTrailDocument?> GetAsync(string auditId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditId);
        ct.ThrowIfCancellationRequested();

        lock (_records)
        {
            var document = _documents.FirstOrDefault(document =>
                string.Equals(document.AuditId, auditId, StringComparison.Ordinal));
            return Task.FromResult(document?.Clone());
        }
    }

    public Task<AuditTrailArtifactWriteResult> UpsertAsync(AuditTrailDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(document.Record);
        ct.ThrowIfCancellationRequested();

        lock (_records)
        {
            var existing = _documents.FirstOrDefault(candidate =>
                string.Equals(candidate.AuditId, document.AuditId, StringComparison.Ordinal));
            if (existing is not null)
            {
                return Task.FromResult(string.Equals(existing.ContentHash, document.ContentHash, StringComparison.Ordinal)
                    ? AuditTrailArtifactWriteResult.Duplicate()
                    : AuditTrailArtifactWriteResult.Conflict());
            }

            _records.Add(document.Record.Clone());
            _documents.Add(document.Clone());
        }

        return Task.FromResult(AuditTrailArtifactWriteResult.Applied());
    }

    public Task<AuditTrailPage> QueryAsync(
        AuditTrailQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        List<AuditRecord> snapshot;
        lock (_records)
        {
            snapshot = _records.Select(static record => record.Clone()).ToList();
        }

        var take = ClampTake(query.Take);
        var offset = DecodeCursor(query.Cursor);
        var filtered = snapshot
            .Where(record => Matches(record, query))
            .OrderByDescending(static record => record.OccurredAt.ToDateTimeOffset())
            .ThenBy(static record => record.AuditId, StringComparer.Ordinal)
            .ToList();

        var pageRecords = filtered
            .Skip(offset)
            .Take(take)
            .Select(static record => record.Clone())
            .ToList();

        var nextOffset = offset + pageRecords.Count;
        var nextCursor = nextOffset < filtered.Count ? EncodeCursor(nextOffset) : null;
        var watermark = filtered.Count == 0
            ? (DateTimeOffset?)null
            : filtered.Max(static record => record.OccurredAt.ToDateTimeOffset());

        return Task.FromResult(new AuditTrailPage(
            pageRecords,
            nextCursor,
            _timeProvider.GetUtcNow(),
            watermark));
    }

    private static bool Matches(AuditRecord record, AuditTrailQuery query)
    {
        return MatchesTime(record, query) &&
               MatchesIdentity(record, query) &&
               MatchesOperation(record, query) &&
               MatchesTarget(record, query) &&
               MatchesCorrelation(record, query) &&
               MatchesCommittedFactReference(record, query);
    }

    private static bool MatchesTime(AuditRecord record, AuditTrailQuery query)
    {
        var occurredAt = record.OccurredAt.ToDateTimeOffset();
        return (!query.OccurredFrom.HasValue || occurredAt >= query.OccurredFrom.Value) &&
               (!query.OccurredTo.HasValue || occurredAt <= query.OccurredTo.Value);
    }

    private static bool MatchesIdentity(AuditRecord record, AuditTrailQuery query)
    {
        return Matches(record.ScopeId, query.ScopeId) &&
               Matches(record.AuditActorId, query.AuditActorId) &&
               Matches(record.IdentityKeyId, query.IdentityKeyId) &&
               (!query.ActorKind.HasValue || record.ActorKind == query.ActorKind.Value);
    }

    private static bool MatchesOperation(AuditRecord record, AuditTrailQuery query)
    {
        return Matches(record.OperationName, query.OperationName) &&
               (!query.OperationKind.HasValue || record.OperationKind == query.OperationKind.Value) &&
               (!query.Outcome.HasValue || record.Outcome == query.Outcome.Value) &&
               (!query.SensitivityLevel.HasValue || record.SensitivityLevel == query.SensitivityLevel.Value) &&
               (!query.CapturePlane.HasValue || record.CapturePlane == query.CapturePlane.Value);
    }

    private static bool MatchesTarget(AuditRecord record, AuditTrailQuery query)
    {
        return Matches(record.Target?.Kind, query.TargetKind) &&
               Matches(record.Target?.Id, query.TargetId);
    }

    private static bool MatchesCorrelation(AuditRecord record, AuditTrailQuery query)
    {
        return Matches(record.Correlation?.TraceId, query.TraceId) &&
               Matches(record.Correlation?.RequestId, query.RequestId) &&
               Matches(record.Correlation?.CommandId, query.CommandId) &&
               Matches(record.Correlation?.CallId, query.CallId) &&
               Matches(record.Correlation?.SessionId, query.SessionId) &&
               Matches(record.Correlation?.WorkflowRunId, query.WorkflowRunId) &&
               Matches(record.Correlation?.ApprovalId, query.ApprovalId);
    }

    private static bool MatchesCommittedFactReference(AuditRecord record, AuditTrailQuery query)
    {
        return Matches(record.CommittedFactRef?.CommittedEventId, query.CommittedEventId) &&
               Matches(record.CommittedFactRef?.ActorId, query.CommittedActorId) &&
               Matches(record.CommittedFactRef?.ActorType, query.CommittedActorType) &&
               Matches(record.CommittedFactRef?.EventTypeUrl, query.CommittedEventTypeUrl) &&
               (!query.CommittedStateVersion.HasValue ||
                record.CommittedFactRef?.StateVersion == query.CommittedStateVersion.Value);
    }

    private static bool Matches(string? actual, string? expected)
    {
        return string.IsNullOrWhiteSpace(expected) || string.Equals(actual, expected.Trim(), StringComparison.Ordinal);
    }

    private static int ClampTake(int take)
    {
        return take <= 0 ? DefaultTake : Math.Min(take, MaxTake);
    }

    private static string EncodeCursor(int offset)
    {
        return Convert.ToBase64String(BitConverter.GetBytes(offset));
    }

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var bytes = Convert.FromBase64String(cursor.Trim());
            return Math.Max(0, BitConverter.ToInt32(bytes, 0));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Audit query cursor is invalid.", nameof(cursor), ex);
        }
    }

    private static AuditTrailDocument ToDocument(AuditRecord record)
    {
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(record.ToByteArray()))
            .ToLowerInvariant();
        var observedAt = record.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return AuditTrailDocumentFactory.Create(record, record.AuditId, contentHash, observedAt);
    }
}
