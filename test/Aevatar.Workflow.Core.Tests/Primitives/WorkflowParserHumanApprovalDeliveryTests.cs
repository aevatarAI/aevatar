using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowParserHumanApprovalDeliveryTests
{
    [Fact]
    public void Parse_WhenHumanApprovalHasDeliveryTargetAndTimeoutDecision_ShouldLiftTypedApprovalOptions()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: approval_delivery
            roles: []
            steps:
              - id: approve
                type: human_approval
                parameters:
                  prompt: "Approve?"
                  delivery_target_id: "${input.approver}"
                  timeout_default_decision: approve
            """);

        var step = workflow.Steps[0];

        step.Presentation?.DeliveryTargetId.Should().Be("${input.approver}");
        step.HumanApprovalOptions?.TimeoutDefaultDecision.Should().Be("approve");
        step.Parameters.Should().ContainKey("prompt");
        step.Parameters.Should().Contain("timeout_default_decision", "approve");
        step.Parameters.Should().NotContainKey("delivery_target_id");
    }

    [Theory]
    [InlineData("human_input")]
    [InlineData("secure_input")]
    public void Parse_WhenHumanInputStepHasDeliveryTarget_ShouldLiftGenericDeliveryTarget(string stepType)
    {
        var workflow = new WorkflowParser().Parse(
            $$"""
              name: input_delivery
              roles: []
              steps:
                - id: input
                  type: {{stepType}}
                  parameters:
                    delivery_target_id: inbox-1
              """);

        var step = workflow.Steps[0];

        step.Presentation?.DeliveryTargetId.Should().Be("inbox-1");
        step.Parameters.Should().NotContainKey("delivery_target_id");
    }
}
