using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Orchestration;

public interface IWorkflowExecutionTopologyResolver
{
    Task<IReadOnlyList<WorkflowTopologyEdge>> ResolveAsync(
        string rootActorId,
        CancellationToken ct = default);
}
