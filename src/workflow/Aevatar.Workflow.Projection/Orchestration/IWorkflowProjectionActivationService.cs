using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionActivationService
    : IProjectionPortActivationService<WorkflowExecutionRuntimeLease>;
