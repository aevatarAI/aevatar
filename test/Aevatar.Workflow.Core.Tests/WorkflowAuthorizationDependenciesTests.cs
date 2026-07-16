using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowAuthorizationDependenciesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name: [")]
    public void EvaluateAuthorizationDependencies_ShouldFailClosedForInvalidDefinition(string yaml)
    {
        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().BeNull();
    }

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
        result.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.NotRequiredNoExternalService);
    }

    [Fact]
    public void EvaluateAuthorizationDependencies_ShouldPreserveStaticNyxIdProxySlugOrderAndMultiplicity()
    {
        var yaml = """
            name: wf-alpha
            roles: []
            steps:
              - id: proxy-a
                type: tool_call
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"slug":"provider-b","path":"/items"}'
              - id: nested
                type: sequence
                children:
                  - id: proxy-b
                    type: tool_call
                    parameters:
                      tool: nyxid_proxy
                      arguments: '{"slug":"provider-a","path":"/items"}'
                  - id: proxy-c
                    type: tool_call
                    parameters:
                      tool: nyxid_proxy
                      arguments: '{"slug":"provider-a","path":"/other"}'
            """;

        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().NotBeNull();
        result!.NyxIdServiceSlugs.Should().Equal("provider-b", "provider-a", "provider-a");
        result.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.Required);
    }

    [Fact]
    public void EvaluateAuthorizationDependencies_ShouldFailClosedForDynamicNyxIdProxySlug()
    {
        var yaml = """
            name: wf-alpha
            roles: []
            steps:
              - id: proxy
                type: tool_call
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"slug":"${route_slug}","path":"/items"}'
            """;

        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().NotBeNull();
        result!.NyxIdServiceSlugs.Should().BeEmpty();
        result.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.Required);
    }

    [Fact]
    public void EvaluateAuthorizationDependencies_ShouldReadStaticNyxIdProxyServiceAlias()
    {
        var yaml = """
            name: wf-alpha
            roles: []
            steps:
              - id: proxy
                type: tool_call
                parameters:
                  tool: nyxid_proxy
                  arguments: '{"service":"provider-alias","path":"/items"}'
            """;

        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().NotBeNull();
        result!.NyxIdServiceSlugs.Should().Equal("provider-alias");
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
