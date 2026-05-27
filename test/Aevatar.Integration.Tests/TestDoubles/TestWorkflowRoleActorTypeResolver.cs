using Aevatar.AI.Core;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Integration.Tests;

internal sealed class TestWorkflowRoleActorTypeResolver : IWorkflowRoleActorTypeResolver
{
    public Type ResolveRoleActorType() => typeof(RoleGAgent);
}
