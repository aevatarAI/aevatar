using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Primitives;

public sealed class WorkflowParserCoverageTests
{
    [Fact]
    public void Parse_WhenYamlIsEmptyOrNameMissing_ShouldThrow()
    {
        var parser = new WorkflowParser();

        Action emptyYaml = () => parser.Parse(string.Empty);
        Action missingName = () => parser.Parse(
            """
            roles: []
            steps: []
            """);

        emptyYaml.Should().Throw<InvalidOperationException>()
            .WithMessage("*YAML 为空*");
        missingName.Should().Throw<InvalidOperationException>()
            .WithMessage("*缺少 name*");
    }

    [Theory]
    [InlineData("wait_signal", true)]
    [InlineData("connector_call", true)]
    [InlineData("secure_connector_call", true)]
    [InlineData("llm_call", true)]
    [InlineData("human_input", true)]
    [InlineData("secure_input", true)]
    [InlineData("human_approval", true)]
    [InlineData("assign", false)]
    public void Parse_WhenRootTimeoutMsIsPresent_ShouldOnlyLiftItForSupportedPrimitiveTypes(string stepType, bool shouldLift)
    {
        var workflow = new WorkflowParser().Parse(
            $$"""
              name: timeout_lift
              roles: []
              steps:
                - id: step_1
                  type: {{stepType}}
                  timeout_ms: 250
              """);

        if (shouldLift)
            workflow.Steps[0].Parameters["timeout_ms"].Should().Be("250");
        else
            workflow.Steps[0].Parameters.Should().NotContainKey("timeout_ms");
    }

    [Fact]
    public void Parse_WhenBranchesUseDictionaryAndListForms_ShouldNormalizeTargets()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: branches
            roles: []
            steps:
              - id: dict_step
                type: conditional
                branches:
                  true:
                    next: done
                  false:
                    target: fallback
              - id: list_step
                type: switch
                branches:
                  - condition: success
                    next: done
                  - when: retry
                    to: fallback
                  - if: ignored
              - id: fallback
                type: assign
                parameters:
                  target: result
                  value: retry
              - id: done
                type: assign
                parameters:
                  target: result
                  value: ok
            """);

        workflow.Steps[0].Branches.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["true"] = "done",
            ["false"] = "fallback",
        });
        workflow.Steps[1].Branches.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["success"] = "done",
            ["retry"] = "fallback",
        });
    }

    [Fact]
    public void Parse_WhenParametersContainScalarsAndCollections_ShouldSerializeInvariantValues()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: serialization
            roles: []
            steps:
              - id: step_1
                type: transform
                parameters:
                  enabled: true
                  ratio: 1.5
                  tags:
                    - alpha
                    - 2
                  config:
                    enabled: false
                    retries: 3
            """);

        workflow.Steps[0].Parameters["enabled"].Should().Be("true");
        workflow.Steps[0].Parameters["ratio"].Should().Be("1.5");
        workflow.Steps[0].Parameters["tags"].Should().Be("""["alpha","2"]""");
        workflow.Steps[0].Parameters["config"].Should().Be("""{"enabled":"false","retries":"3"}""");
    }

    [Fact]
    public void Parse_WhenRetryAndOnErrorUseDefaultsAndFallbackAlias_ShouldNormalizePolicies()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: retry_defaults
            roles: []
            steps:
              - id: step_1
                type: transform
                retry: {}
                on_error:
                  strategy: continue
                  fallback: fallback_step
                  default_output: recovered
              - id: fallback_step
                type: assign
                parameters:
                  target: result
                  value: ok
            """);

        var step = workflow.Steps[0];
        step.Retry.Should().NotBeNull();
        step.Retry!.MaxAttempts.Should().Be(3);
        step.Retry.Backoff.Should().Be("fixed");
        step.Retry.DelayMs.Should().Be(1000);
        step.OnError.Should().NotBeNull();
        step.OnError!.Strategy.Should().Be("continue");
        step.OnError.FallbackStep.Should().Be("fallback_step");
        step.OnError.DefaultOutput.Should().Be("recovered");
    }

    [Fact]
    public void Parse_WhenStepIdIsMissing_ShouldThrow()
    {
        Action act = () => new WorkflowParser().Parse(
            """
            name: missing_step_id
            roles: []
            steps:
              - type: transform
            """);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*step 缺 id*");
    }
}
