using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowStepTargetAgentResolverAdditionalTests
{
    [Fact]
    public void ResolveEffectiveTargetRole_WhenLlmCallOmitsTargetRole_ShouldUseImplicitAssistant()
    {
        var role = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(
            workflow: null,
            configuredTargetRole: null,
            stepType: "llm_call");

        role.Should().Be(WorkflowImplicitLlmRolePolicy.DefaultRoleId);
    }
}
