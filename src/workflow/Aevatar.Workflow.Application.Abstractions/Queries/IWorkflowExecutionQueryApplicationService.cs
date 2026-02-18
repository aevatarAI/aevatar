namespace Aevatar.Workflow.Application.Abstractions.Queries;

public interface IWorkflowExecutionQueryApplicationService
{
    bool ActorQueryEnabled { get; }

    Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default);

    IReadOnlyList<string> ListWorkflows();

    Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default);
}
