using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public class WorkflowDefinitionNormalizerTests
{
    [Fact]
    public void Normalize_ShouldApplyAliasDefaultsAndRootParameterLift()
    {
        var raw = new WorkflowRawDefinition
        {
            Name = "normalize_demo",
            Roles = [],
            Steps =
            [
                new WorkflowRawStep
                {
                    Id = "http_1",
                    Type = "http_get",
                    Parameters = new Dictionary<string, object?>
                    {
                        ["connector"] = "demo_http",
                    },
                },
                new WorkflowRawStep
                {
                    Id = "wait_1",
                    Type = "wait_signal",
                    TimeoutMs = 2000,
                    Parameters = new Dictionary<string, object?>(),
                },
            ],
        };

        var workflow = new WorkflowDefinitionNormalizer().Normalize(raw);

        workflow.Steps[0].Type.Should().Be("connector_call");
        workflow.Steps[0].Parameters["method"].Should().Be("GET");
        workflow.Steps[1].Parameters["timeout_ms"].Should().Be("2000");
    }
}
