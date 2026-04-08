using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowYamlBundleNormalizerTests
{
    private readonly WorkflowParser _parser = new();

    [Fact]
    public void Normalize_WhenWhileStepHasChildren_ShouldRewriteToWorkflowCallAndEmitSyntheticInlineWorkflow()
    {
        var yaml = """
            name: root_flow
            roles:
              - id: researcher
                name: Researcher
                system_prompt: "helpful"
            steps:
              - id: research_loop
                type: while
                parameters:
                  max_iterations: "2"
                children:
                  - id: pull
                    type: transform
                    parameters:
                      op: trim
                    next: write
                  - id: write
                    type: assign
                    parameters:
                      target: answer
                      value: "$input"
            """;

        var result = new WorkflowYamlBundleNormalizer().Normalize(yaml);
        var normalizedRoot = _parser.Parse(result.WorkflowYaml);
        var whileStep = normalizedRoot.Steps.Should().ContainSingle().Subject;

        whileStep.Type.Should().Be("while");
        whileStep.Children.Should().BeNull();
        whileStep.Parameters["step"].Should().Be("workflow_call");
        whileStep.Parameters["sub_param_lifecycle"].Should().Be("transient");

        var syntheticWorkflowName = whileStep.Parameters["sub_param_workflow"];
        result.InlineWorkflowYamls.Should().ContainKey(syntheticWorkflowName);

        var syntheticWorkflow = _parser.Parse(result.InlineWorkflowYamls[syntheticWorkflowName]);
        syntheticWorkflow.Name.Should().Be(syntheticWorkflowName);
        syntheticWorkflow.Roles.Should().ContainSingle().Which.Id.Should().Be("researcher");
        syntheticWorkflow.Steps.Select(x => x.Id).Should().ContainInOrder("pull", "write");
        syntheticWorkflow.Steps[0].Next.Should().Be("write");
    }

    [Fact]
    public void Normalize_WhenWhileChildrenNestedInsideSyntheticWorkflow_ShouldEmitNestedSyntheticWorkflow()
    {
        var yaml = """
            name: nested_root
            steps:
              - id: outer_loop
                type: while
                parameters:
                  max_iterations: "2"
                children:
                  - id: inner_loop
                    type: while
                    parameters:
                      max_iterations: "3"
                    children:
                      - id: normalize
                        type: transform
                        parameters:
                          op: trim
            """;

        var result = new WorkflowYamlBundleNormalizer().Normalize(yaml);
        var root = _parser.Parse(result.WorkflowYaml);
        var outerWorkflowName = root.Steps.Single().Parameters["sub_param_workflow"];

        result.InlineWorkflowYamls.Should().ContainKey(outerWorkflowName);
        result.InlineWorkflowYamls.Count.Should().Be(2);

        var outerWorkflow = _parser.Parse(result.InlineWorkflowYamls[outerWorkflowName]);
        var innerLoop = outerWorkflow.Steps.Should().ContainSingle().Subject;
        innerLoop.Children.Should().BeNull();
        innerLoop.Parameters["step"].Should().Be("workflow_call");
        innerLoop.Parameters["sub_param_lifecycle"].Should().Be("transient");
        result.InlineWorkflowYamls.Should().ContainKey(innerLoop.Parameters["sub_param_workflow"]);
    }

    [Fact]
    public void Normalize_WhenWhileChildrenCombinedWithExplicitStepParameter_ShouldThrow()
    {
        var yaml = """
            name: invalid_root
            steps:
              - id: bad_loop
                type: while
                parameters:
                  step: llm_call
                children:
                  - id: child
                    type: transform
                    parameters:
                      op: trim
            """;

        var act = () => new WorkflowYamlBundleNormalizer().Normalize(yaml);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*while.children*parameters.step*");
    }

    [Fact]
    public void Normalize_WhenInlineWorkflowKeyDiffersFromYamlName_ShouldPreserveOriginalKey()
    {
        var yaml = """
            name: root_flow
            steps:
              - id: research_loop
                type: while
                parameters:
                  max_iterations: "2"
                children:
                  - id: trim
                    type: transform
                    parameters:
                      op: trim
            """;

        var result = new WorkflowYamlBundleNormalizer().Normalize(
            yaml,
            new Dictionary<string, string>
            {
                ["child.yaml"] = """
                    name: child_flow
                    steps:
                      - id: noop
                        type: transform
                        parameters:
                          op: trim
                    """,
            });

        result.InlineWorkflowYamls.Should().ContainKey("child.yaml");
        result.InlineWorkflowYamls.Should().NotContainKey("child_flow");
        result.InlineWorkflowYamls.Keys.Should().Contain(key => key.StartsWith("__inline__root_flow__research_loop", StringComparison.Ordinal));
    }
}
