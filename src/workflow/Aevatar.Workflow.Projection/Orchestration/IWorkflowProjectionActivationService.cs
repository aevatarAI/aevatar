using Aevatar.CQRS.Projection.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionActivationService
    : IProjectionPortActivationService<WorkflowExecutionRuntimeLease>;
