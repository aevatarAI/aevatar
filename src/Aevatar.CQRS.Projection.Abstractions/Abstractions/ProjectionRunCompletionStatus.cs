namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Completion status of one projection run.
/// </summary>
public enum ProjectionRunCompletionStatus
{
    Completed = 0,
    TimedOut = 1,
    Failed = 2,
    Stopped = 3,
    NotFound = 4,
    Disabled = 5,
}
