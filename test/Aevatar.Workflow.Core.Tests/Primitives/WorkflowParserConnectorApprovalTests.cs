using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowParserConnectorApprovalTests
{
    [Theory]
    [InlineData("connector_call")]
    [InlineData("secure_connector_call")]
    public void Parse_WhenConnectorApprovalIsRequired_ShouldLiftTypedOptions(string stepType)
    {
        var workflow = new WorkflowParser().Parse(
            $$"""
              name: connector_approval
              roles: []
              steps:
                - id: invoke
                  type: {{stepType}}
                  parameters:
                    connector: service_proxy
                    operation: create_resource
                    approval.policy: required
                    approval.service_ref: "${input.service_ref}"
                    approval.node_id: node-alpha
                    approval.http_verb: post
                    approval.resource: /resources/alpha
                    approval.permission_scope: resources.write
                    approval.expiration_seconds: 300
                    approval.status_check_interval_seconds: 3
                    approval.destructive: true
                    approval.team_id: team-alpha
                    approval.member_id: member-alpha
                    approval.workflow_id: workflow-alpha
                    approval.published_service_id: published-service-alpha
                    approval.policy_reason: external-write
              """);

        var step = workflow.Steps.Should().ContainSingle().Subject;
        step.ConnectorApprovalOptions.Should().NotBeNull();
        var approval = step.ConnectorApprovalOptions!;
        approval.ServiceRef.Should().Be("${input.service_ref}");
        approval.NodeId.Should().Be("node-alpha");
        approval.HttpVerb.Should().Be("post");
        approval.Resource.Should().Be("/resources/alpha");
        approval.PermissionScope.Should().Be("resources.write");
        approval.ExpirationSeconds.Should().Be(300);
        approval.StatusCheckIntervalSeconds.Should().Be(3);
        approval.Destructive.Should().BeTrue();
        approval.TeamId.Should().Be("team-alpha");
        approval.MemberId.Should().Be("member-alpha");
        approval.WorkflowId.Should().Be("workflow-alpha");
        approval.PublishedServiceId.Should().Be("published-service-alpha");
        approval.PolicyReason.Should().Be("external-write");
        step.Parameters.Should().Contain("connector", "service_proxy");
    }

    [Theory]
    [InlineData("connector_call", "optional")]
    [InlineData("transform", "required")]
    public void Parse_WhenApprovalIsNotRequiredForConnector_ShouldNotCreateTypedOptions(
        string stepType,
        string policy)
    {
        var workflow = new WorkflowParser().Parse(
            $$"""
              name: connector_without_approval
              roles: []
              steps:
                - id: invoke
                  type: {{stepType}}
                  parameters:
                    approval.policy: {{policy}}
              """);

        workflow.Steps.Should().ContainSingle()
            .Which.ConnectorApprovalOptions.Should().BeNull();
    }
}
