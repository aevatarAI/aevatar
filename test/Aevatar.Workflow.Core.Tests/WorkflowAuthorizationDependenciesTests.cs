using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowAuthorizationDependenciesTests
{
    [Fact]
    public void EvaluateAuthorizationDependencies_ShouldCollectRoleAndNestedConnectorDependencies()
    {
        var yaml = """
            name: wf-alpha
            roles:
              - id: analyst
                name: Analyst
                connectors: [ " Calendar ", "calendar" ]
            steps:
              - id: parent
                type: sequence
                children:
                  - id: nested-call
                    type: connector_call
                    parameters:
                      connector: Mail
            """;

        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().NotBeNull();
        result!.ConnectorCapabilityRefs.Should().Equal("Calendar", "Mail");
        result.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.Required);
    }

    [Theory]
    [InlineData("llm_call", true)]
    [InlineData("transform", false)]
    public void EvaluateAuthorizationDependencies_ShouldDescribeLlmAndNoExternalServicePolicy(
        string stepType,
        bool ownerLlmRequired)
    {
        var yaml = $$"""
            name: wf-alpha
            roles: []
            steps:
              - id: step-alpha
                type: {{stepType}}
            """;

        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().NotBeNull();
        result!.OwnerLlmRouteRequired.Should().Be(ownerLlmRequired);
        result.ConnectorCapabilityRefs.Should().BeEmpty();
        result.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.NotRequiredNoExternalService);
    }
}
