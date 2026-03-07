using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public class WorkflowYamlDeserializerTests
{
    [Fact]
    public void Deserialize_ShouldBindRawWorkflowShape()
    {
        var yaml = """
            name: raw_demo
            description: demo
            roles:
              - id: planner
                provider: openai
            steps:
              - id: gate
                type: conditional
                parameters:
                  condition: "ready"
            """;

        var raw = new WorkflowYamlDeserializer().Deserialize(yaml);

        raw.Name.Should().Be("raw_demo");
        raw.Description.Should().Be("demo");
        raw.Roles.Should().ContainSingle();
        raw.Roles![0].Id.Should().Be("planner");
        raw.Steps.Should().ContainSingle();
        raw.Steps![0].Type.Should().Be("conditional");
    }
}
