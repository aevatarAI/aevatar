using Aevatar.AI.Core;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Integration.AI;

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old pattern: Workflow.Core depended on the concrete AI RoleGAgent type. New principle: Integration.AI resolves the role actor type at the boundary.
public sealed class WorkflowAiRoleActorTypeResolver : IWorkflowRoleActorTypeResolver
{
    public Type ResolveRoleActorType() => typeof(RoleGAgent);
}
