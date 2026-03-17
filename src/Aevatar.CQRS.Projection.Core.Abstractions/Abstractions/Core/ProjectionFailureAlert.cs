namespace Aevatar.CQRS.Projection.Core.Abstractions;

public sealed record ProjectionFailureAlert(
    ProjectionRuntimeScopeKey ScopeKey,
    string FailureId,
    string Stage,
    string EventId,
    string EventType,
    long SourceVersion,
    string Reason,
    int FailureCount,
    DateTimeOffset OccurredAt);
