using Aevatar.CQRS.Projections.Abstractions.ReadModels;

namespace Aevatar.CQRS.Projections.Abstractions;

/// <summary>
/// Read-model store for chat run projections.
/// </summary>
public interface IWorkflowExecutionReadModelStore
    : IProjectionReadModelStore<WorkflowExecutionReport, string>;
