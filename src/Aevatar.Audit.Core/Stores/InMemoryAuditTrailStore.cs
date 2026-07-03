using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.Sanitization;

namespace Aevatar.Audit.Core.Stores;

public sealed class InMemoryAuditTrailStore : IAuditTrailAppender, IAuditTrailQueryPort
{
    private const int DefaultTake = 100;
    private const int MaxTake = 500;

    private readonly AuditRecordSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;
    private readonly List<AuditRecord> _records = [];

    public InMemoryAuditTrailStore(
        AuditRecordSanitizer? sanitizer = null,
        TimeProvider? timeProvider = null)
    {
        _sanitizer = sanitizer ?? new AuditRecordSanitizer();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<AuditTrailAppendReceipt> AppendAsync(
        AuditRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sanitized = _sanitizer.Sanitize(record);
        lock (_records)
        {
            _records.Add(sanitized.Clone());
        }

        var receipt = new AuditTrailAppendReceipt(
            sanitized.AuditId,
            sanitized.AuditActorId,
            sanitized.OccurredAt.ToDateTimeOffset());

        return Task.FromResult(receipt);
    }

    public async Task<IReadOnlyList<AuditTrailAppendReceipt>> AppendManyAsync(
        IReadOnlyList<AuditRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var receipts = new List<AuditTrailAppendReceipt>(records.Count);
        foreach (var record in records)
        {
            receipts.Add(await AppendAsync(record, cancellationToken));
        }

        return receipts;
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
            .OrderBy(static record => record.OccurredAt.ToDateTimeOffset())
            .ThenBy(static record => record.AuditId, StringComparer.Ordinal)
            .ToList();

        var pageRecords = filtered
            .Skip(offset)
            .Take(take)
            .Select(static record => record.Clone())
            .ToList();

        var nextOffset = offset + pageRecords.Count;
        var nextCursor = nextOffset < filtered.Count ? EncodeCursor(nextOffset) : null;
        var watermark = snapshot.Count == 0
            ? (DateTimeOffset?)null
            : snapshot.Max(static record => record.OccurredAt.ToDateTimeOffset());

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
}
