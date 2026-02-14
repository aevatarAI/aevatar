using Aevatar.CQRS.Projections.Abstractions.ReadModels;

namespace Aevatar.CQRS.Projections.Abstractions;

/// <summary>
/// Chat-run projection coordinator abstraction.
/// </summary>
public interface IWorkflowExecutionProjectionCoordinator
    : IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>;
