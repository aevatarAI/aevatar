using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowExecutionMaterializationScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<WorkflowExecutionMaterializationContext>;

internal sealed class WorkflowBindingMaterializationScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<WorkflowBindingProjectionContext>;

internal sealed class WorkflowExecutionSessionScopeGAgent
    : ProjectionSessionScopeGAgentBase<WorkflowExecutionProjectionContext>;
