using Aevatar.AI.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;

namespace Aevatar.Workflow.Integration.AI.Tests;

public sealed class WorkflowAiRoleActorTypeResolverTests
{
    [Fact]
    public void ResolveRoleActorType_ShouldReturnRoleGAgentType()
    {
        var resolver = new WorkflowAiRoleActorTypeResolver();

        resolver.ResolveRoleActorType().Should().Be(typeof(RoleGAgent));
    }

    [Fact]
    public void Resolver_ShouldImplementWorkflowRoleActorTypeResolverContract()
    {
        var resolver = new WorkflowAiRoleActorTypeResolver();

        resolver.Should().BeAssignableTo<IWorkflowRoleActorTypeResolver>();
        resolver.ResolveRoleActorType().Should().BeAssignableTo(typeof(Aevatar.Foundation.Abstractions.IAgent));
    }
}
