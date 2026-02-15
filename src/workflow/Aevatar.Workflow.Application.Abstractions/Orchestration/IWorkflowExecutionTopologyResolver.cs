using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Application.Abstractions.Orchestration;

public interface IWorkflowExecutionTopologyResolver
{
    Task<IReadOnlyList<WorkflowExecutionTopologyEdge>> ResolveAsync(
        IActorRuntime runtime,
        string rootActorId,
        CancellationToken ct = default);
}
