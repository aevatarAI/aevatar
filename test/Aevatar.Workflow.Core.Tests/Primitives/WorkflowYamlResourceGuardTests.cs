using System.Text;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowYamlResourceGuardTests
{
    [Fact]
    public void Parse_WhenChildrenDepthIsBelowLimit_ShouldSucceed()
    {
        var yaml = BuildNestedWorkflow(childLinks: 30);

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Name.Should().Be("nested");
    }

    [Fact]
    public void Parse_WhenChildrenDepthExceedsLimit_ShouldRejectBeforeDeserialization()
    {
        var yaml = BuildNestedWorkflow(childLinks: 31);

        var act = () => new WorkflowParser().Parse(yaml);

        var exception = act.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.NestingDepth);
        exception.Actual.Should().Be(65);
        exception.Maximum.Should().Be(WorkflowYamlResourceGuard.MaxNestingDepth);
    }

    [Fact]
    public void Parse_WhenUtf8BytesExceedLimit_ShouldRejectWithTypedLimit()
    {
        var yaml = $"name: oversized\ndescription: {new string('a', WorkflowYamlResourceGuard.MaxUtf8Bytes)}\nroles: []\nsteps: []\n";

        var act = () => new WorkflowParser().Parse(yaml);

        var exception = act.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.Utf8Bytes);
        exception.Actual.Should().BeGreaterThan(WorkflowYamlResourceGuard.MaxUtf8Bytes);
        exception.Maximum.Should().Be(WorkflowYamlResourceGuard.MaxUtf8Bytes);
    }

    [Fact]
    public void Parse_WhenNodeCountExceedsLimit_ShouldRejectWithTypedLimit()
    {
        var parameters = string.Join(
            '\n',
            Enumerable.Range(0, 5_100).Select(index => $"      key_{index}: value_{index}"));
        var yaml = $"name: nodes\nroles: []\nsteps:\n  - id: assign\n    type: assign\n    parameters:\n{parameters}\n";

        var act = () => new WorkflowParser().Parse(yaml);

        var exception = act.Should().Throw<WorkflowYamlResourceLimitException>().Which;
        exception.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.Nodes);
        exception.Actual.Should().Be(WorkflowYamlResourceGuard.MaxNodes + 1);
        exception.Maximum.Should().Be(WorkflowYamlResourceGuard.MaxNodes);
    }

    internal static string BuildNestedWorkflow(int childLinks)
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
