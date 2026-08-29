using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowExecutionProjectionScopeRecoveryAgentKindResolver
    : IProjectionScopeRecoveryAgentKindResolver
{
    public bool TryResolve(
        ProjectionRuntimeScopeKey scopeKey,
        out string agentKind)
    {
        if (scopeKey.Mode == ProjectionRuntimeMode.DurableMaterialization &&
            string.Equals(
                scopeKey.ProjectionKind,
                WorkflowProjectionKinds.ExecutionMaterialization,
                StringComparison.Ordinal))
        {
            agentKind = WorkflowExecutionMaterializationScopeGAgent.AgentKind;
            return true;
        }

        agentKind = string.Empty;
        return false;
    }
}
