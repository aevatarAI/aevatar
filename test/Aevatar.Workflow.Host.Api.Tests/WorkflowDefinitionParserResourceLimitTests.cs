using System.Text;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowDefinitionParserResourceLimitTests
{
    [Fact]
    public async Task ParseWorkflowYamlAsync_ShouldClassifyResourceLimit()
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseWorkflowYamlAsync(BuildNestedWorkflowYaml(childLinks: 31));

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.ResourceLimit);
        result.Error.Should().Contain("nesting depth");
    }

    [Fact]
    public async Task ParseInlineWorkflowBundleAsync_ShouldPropagateResourceLimit()
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseInlineWorkflowBundleAsync(
            [new WorkflowChatInlineYamlDocument(string.Empty, BuildNestedWorkflowYaml(childLinks: 31))]);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.ResourceLimit);
        result.Error.Should().Contain("nesting depth");
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_ShouldClassifyCollectionAliasCycle()
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseWorkflowYamlAsync(CyclicWorkflowYaml);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.ResourceLimit);
        result.Error.Should().Contain("nesting depth");
    }

    [Fact]
    public async Task ParseInlineWorkflowBundleAsync_ShouldPropagateCollectionAliasCycle()
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseInlineWorkflowBundleAsync(
            [new WorkflowChatInlineYamlDocument(string.Empty, CyclicWorkflowYaml)]);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.ResourceLimit);
        result.Error.Should().Contain("nesting depth");
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_ShouldClassifyForwardCollectionAliasCycle()
    {
        var parser = new WorkflowDefinitionParser([new WorkflowCoreModulePack()]);

        var result = await parser.ParseWorkflowYamlAsync(ForwardCyclicWorkflowYaml);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.ResourceLimit);
        result.Error.Should().Contain("nesting depth");
    }

    private const string CyclicWorkflowYaml = """
                                                name: cyclic
                                                roles: []
                                                steps: &steps
                                                  - id: loop
                                                    type: assign
                                                    children: *steps
                                                """;

    private const string ForwardCyclicWorkflowYaml = """
                                                       name: forward-cycle
                                                       steps: *a
                                                       roles: &a
                                                         - id: a
                                                           type: assign
                                                           children: *b
                                                       configuration: &b
                                                         - id: b
                                                           type: assign
                                                           children: *a
                                                       """;

    private static string BuildNestedWorkflowYaml(int childLinks)
    {
        var yaml = new StringBuilder()
            .AppendLine("name: nested")
            .AppendLine("roles: []")
            .AppendLine("steps:");

        for (var index = 0; index <= childLinks; index++)
        {
            var itemIndent = new string(' ', 2 + (index * 4));
            var propertyIndent = new string(' ', 4 + (index * 4));
            yaml.Append(itemIndent).Append("- id: step_").AppendLine(index.ToString());
            yaml.Append(propertyIndent).AppendLine("type: assign");
            if (index < childLinks)
                yaml.Append(propertyIndent).AppendLine("children:");
        }

        return yaml.ToString();
    }
}
