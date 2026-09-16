using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.Projection;
using Aevatar.Audit.Core.Sanitization;

namespace Aevatar.Audit.Core.Stores;

public sealed class InMemoryAuditTrailStore : IAuditTrailAppender, IAuditTrailQueryPort, IAuditTrailArtifactStore
{
    private const int DefaultTake = 100;
    private const int MaxTake = 500;

    private readonly AuditRecordSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;
    private readonly List<AuditRecord> _records = [];
    private readonly List<AuditTrailDocument> _documents = [];
    private DateTimeOffset? _ingestionWatermark;

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

        var sanitizedRecord = _sanitizer.Sanitize(document.Record);
        var sanitizedDocument = ToDocument(sanitizedRecord);

        lock (_records)
        {
            var existing = _documents.FirstOrDefault(candidate =>
                string.Equals(candidate.AuditId, sanitizedDocument.AuditId, StringComparison.Ordinal));
            if (existing is not null)
            {
                var isDuplicate = string.Equals(
                    existing.ContentHash,
                    sanitizedDocument.ContentHash,
                    StringComparison.Ordinal) ||
                    existing.Record is not null &&
                    sanitizedDocument.Record is not null &&
                    AuditRecordSemanticComparer.AreEquivalent(existing.Record, sanitizedDocument.Record);
                return Task.FromResult(isDuplicate
                    ? AuditTrailArtifactWriteResult.Duplicate()
                    : AuditTrailArtifactWriteResult.Conflict());
            }

            _records.Add(sanitizedRecord.Clone());
            _documents.Add(sanitizedDocument);
            var recordedAt = sanitizedRecord.RecordedAt.ToDateTimeOffset();
            if (!_ingestionWatermark.HasValue || recordedAt > _ingestionWatermark.Value)
                _ingestionWatermark = recordedAt;
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
        DateTimeOffset? ingestionWatermark;
        lock (_records)
        {
            snapshot = _records.Select(static record => record.Clone()).ToList();
            ingestionWatermark = _ingestionWatermark;
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
        var coverage = AuditQueryCoverage.Create(
            query,
            nextCursor is not null,
            ingestionWatermark,
            completeThrough: null,
            schemaCompatibility: ResolveSchemaCompatibility(filtered));

        return Task.FromResult(new AuditTrailPage(
            pageRecords,
            nextCursor,
            _timeProvider.GetUtcNow(),
            coverage));
    }

    private static bool Matches(AuditRecord record, AuditTrailQuery query)
    {
        return MatchesTime(record, query) &&
               MatchesIdentity(record, query) &&
               MatchesOperation(record, query) &&
               MatchesChat(record, query) &&
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
               MatchesAny(record.AuditActorId, query.AuditActorIds) &&
               Matches(record.IdentityKeyId, query.IdentityKeyId) &&
               (!query.ActorKind.HasValue || record.ActorKind == query.ActorKind.Value);
    }

    private static bool MatchesChat(AuditRecord record, AuditTrailQuery query)
    {
        var chat = record.Provenance?.Chat;
        return (!query.RequireChatProvenance || chat is not null) &&
               (!query.ChatSurface.HasValue || chat?.Surface == query.ChatSurface.Value) &&
               Matches(chat?.ConversationId, query.ChatConversationId);
    }

    private static bool MatchesOperation(AuditRecord record, AuditTrailQuery query)
    {
        return Matches(record.OperationName, query.OperationName) &&
               (!query.OperationKind.HasValue || record.OperationKind == query.OperationKind.Value) &&
               (!query.Outcome.HasValue || record.Outcome == query.Outcome.Value) &&
               (!query.LifecyclePhase.HasValue ||
                AuditContractSemantics.ResolveLifecyclePhase(record) == query.LifecyclePhase.Value) &&
               (!query.TerminalOutcome.HasValue ||
                AuditContractSemantics.ResolveTerminalOutcome(record) == query.TerminalOutcome.Value) &&
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
               Matches(record.Correlation?.CorrelationId, query.CorrelationId) &&
               Matches(record.Correlation?.CausationId, query.CausationId) &&
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

    private static bool MatchesAny(string? actual, IReadOnlyList<string>? expected)
    {
        return expected is null || expected.Any(value =>
            !string.IsNullOrWhiteSpace(value) &&
            string.Equals(actual, value.Trim(), StringComparison.Ordinal));
    }

    private static int ClampTake(int take)
    {
        return take <= 0 ? DefaultTake : Math.Min(take, MaxTake);
    }

    private static AuditSchemaCompatibility ResolveSchemaCompatibility(IEnumerable<AuditRecord> records)
    {
        var compatibility = AuditSchemaCompatibility.Current;
        foreach (var record in records)
        {
            switch (AuditContractSemantics.GetSchemaCompatibility(record))
            {
                case AuditRecordSchemaCompatibility.Incompatible:
                    return AuditSchemaCompatibility.Incompatible;
                case AuditRecordSchemaCompatibility.LegacyMapped:
                    compatibility = AuditSchemaCompatibility.ContainsLegacyRecords;
                    break;
            }
        }

        return compatibility;
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
        var contentHash = AuditRecordContentHasher.Compute(record);
        var observedAt = record.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return AuditTrailDocumentFactory.Create(record, record.AuditId, contentHash, observedAt);
    }
}
