namespace Aevatar.Workflow.Application.Abstractions.Runs;

/// <summary>
/// Capability-level workflow execution entry point used by sibling capabilities (for example Maker).
/// </summary>
public interface IWorkflowExecutionCapability
{
    Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowExecutionRequest request,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask>? emitAsync = null,
        CancellationToken ct = default);
}

public sealed record WorkflowExecutionRequest(
    string Input,
    string WorkflowName,
    string WorkflowYaml,
    string? ActorId = null,
    TimeSpan? Timeout = null,
    bool DestroyActorAfterRun = false);

public sealed record WorkflowExecutionResult(
    string ActorId,
    string WorkflowName,
    string CommandId,
    DateTimeOffset StartedAt,
    string Output,
    bool Success,
    bool TimedOut,
    string? Error);
