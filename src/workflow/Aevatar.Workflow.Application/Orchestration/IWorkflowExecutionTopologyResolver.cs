using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Orchestration;

public interface IWorkflowExecutionTopologyResolver
{
    Task<IReadOnlyList<WorkflowRunTopologyEdge>> ResolveAsync(
        IActorRuntime runtime,
        string rootActorId,
        CancellationToken ct = default);
}
