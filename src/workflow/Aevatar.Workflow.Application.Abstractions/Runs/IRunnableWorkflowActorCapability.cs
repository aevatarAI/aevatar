namespace Aevatar.Workflow.Application.Abstractions.Runs;

/// <summary>
/// Stable capability contract for running a workflow actor from sibling capabilities.
/// </summary>
public interface IRunnableWorkflowActorCapability
{
    Task<RunnableWorkflowActorResult> RunAsync(
        RunnableWorkflowActorRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask>? emitAsync = null,
        CancellationToken ct = default);
}

public sealed record RunnableWorkflowActorRequest(
    string Input,
    string WorkflowName,
    string WorkflowYaml,
    string? ActorId = null,
    TimeSpan? Timeout = null,
    bool DestroyActorAfterRun = false);

public sealed record RunnableWorkflowActorResult(
    string ActorId,
    string WorkflowName,
    string CommandId,
    DateTimeOffset StartedAt,
    string Output,
    bool Success,
    bool TimedOut,
    string? Error);
