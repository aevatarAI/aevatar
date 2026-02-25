using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public class WorkflowParserConfigurationTests
{
    [Fact]
    public void Parse_WhenClosedWorldModeEnabled_ShouldBindConfiguration()
    {
        var yaml = """
            name: closed_world
            configuration:
              closed_world_mode: true
            roles: []
            steps:
              - id: s1
                type: assign
                parameters:
                  target: x
                  value: "1"
            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Configuration.ClosedWorldMode.Should().BeTrue();
    }

    [Fact]
    public void Parse_WhenConfigurationMissing_ShouldUseDefaultClosedWorldModeFalse()
    {
        var yaml = """
            name: open_world
            roles: []
            steps:
              - id: s1
                type: assign
                parameters:
                  target: x
                  value: "1"
            """;

        var workflow = new WorkflowParser().Parse(yaml);

        workflow.Configuration.ClosedWorldMode.Should().BeFalse();
    }
}
