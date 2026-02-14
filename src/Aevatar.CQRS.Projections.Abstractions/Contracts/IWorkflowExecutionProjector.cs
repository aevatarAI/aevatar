using Aevatar.CQRS.Projections.Abstractions.ReadModels;

namespace Aevatar.CQRS.Projections.Abstractions;

/// <summary>
/// Chat-run projector abstraction.
/// </summary>
public interface IWorkflowExecutionProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>;
