namespace Aevatar.Workflow.Application.Abstractions.Runs;

public sealed record WorkflowRunDetachedCleanupRequest(
    string ActorId,
    string WorkflowName,
    string CommandId,
    IReadOnlyList<string> CreatedActorIds);

public interface IWorkflowRunDetachedCleanupScheduler
{
    Task ScheduleAsync(
        WorkflowRunDetachedCleanupRequest request,
        CancellationToken ct = default);
}
