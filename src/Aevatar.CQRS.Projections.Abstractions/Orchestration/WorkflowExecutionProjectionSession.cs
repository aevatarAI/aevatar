namespace Aevatar.CQRS.Projections.Abstractions;

/// <summary>
/// Run-scoped projection session.
/// </summary>
public sealed class WorkflowExecutionProjectionSession
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public WorkflowExecutionProjectionContext? Context { get; init; }

    public bool Enabled => Context != null;
}
