using Aevatar.AI.Abstractions.Agents;

namespace Aevatar.Workflow.Integration.AI;

public sealed class WorkflowRoleGAgentTypeResolver : IRoleAgentTypeResolver
{
    public Type ResolveRoleAgentType() => typeof(WorkflowRoleGAgent);
}
