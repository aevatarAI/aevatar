using Aevatar.CQRS.Projections.Abstractions.ReadModels;

namespace Aevatar.CQRS.Projections.Abstractions;

/// <summary>
/// Chat-run reducer abstraction.
/// </summary>
public interface IWorkflowExecutionEventReducer
    : IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>;
