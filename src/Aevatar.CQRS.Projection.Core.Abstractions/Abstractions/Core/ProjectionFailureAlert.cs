namespace Aevatar.CQRS.Projection.Core.Abstractions;

public enum ProjectionFailureAlertKind
{
    FailureRecorded = 0,
    DiagnosticRetentionDropped = 1,
}

public sealed record ProjectionFailureAlert(
    ProjectionFailureAlertKind Kind,
    ProjectionRuntimeScopeKey ScopeKey,
    string FailureId,
    string Stage,
    string EventId,
    string EventType,
    long SourceVersion,
    string Reason,
    int UnresolvedFailureCount,
    int DroppedCount,
    IReadOnlyList<string> DroppedFailureIds,
    long DiagnosticDroppedTotal,
    DateTimeOffset OccurredAt);
