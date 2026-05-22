using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowStepTargetAgentResolverAdditionalTests
{
    [Fact]
    public void ResolveEffectiveTargetRole_WhenLlmCallHasLegacyAgentTypeParameter_ShouldStillUseImplicitAssistant()
    {
        var parameters = new MapField<string, string>
        {
            ["agent_type"] = "LegacyClrTypeName",
        };

        var role = WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(
            workflow: null,
            configuredTargetRole: null,
            stepType: "llm_call",
            parameters: parameters);

        role.Should().Be(WorkflowImplicitLlmRolePolicy.DefaultRoleId);
    }
}
