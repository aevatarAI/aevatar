using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Xunit;

namespace Aevatar.Workflow.Core.Tests.Execution;

/// <summary>
/// The probe must recognize the two structural shapes that consume the run
/// input as bounded JSON — an entry capture feeding a transform, and a
/// <c>.json</c> accessor applied to the capture — while leaving free-text
/// workflows (LLM-first, pass-through) untouched.
/// </summary>
public class WorkflowRunInputContractTests
{
    [Fact]
    public void RequiresJsonInput_WhenEntryCaptureFeedsTransform_ShouldBeTrue()
    {
        // Structured input is captured before it feeds a transform template.
        var definition = Parse(
            """
            name: probe_capture_then_transform
            steps:
              - id: capture_request
                type: assign
                parameters:
                  target: raw_request
                  value: "$input"
                next: normalize_request
              - id: normalize_request
                type: transform
                parameters:
                  op: template
                  template: "{{ json({ ok: true }) }}"
            """);

        WorkflowRunInputContract.RequiresJsonInput(definition).Should().BeTrue();
    }

    [Fact]
    public void RequiresJsonInput_WhenSwitchUsesJsonAccessorOnCapture_ShouldBeTrue()
    {
        // Structured input is captured before a switch reads one of its JSON
        // fields, with an unrelated literal configuration step in between.
        var definition = Parse(
            """
            name: probe_json_accessor
            steps:
              - id: capture_request
                type: assign
                parameters:
                  target: raw_request
                  value: "$input"
                next: config
              - id: config
                type: assign
                parameters:
                  target: deploy_config
                  value: "{\"company_domain\":\"example.io\"}"
                next: route_mode
              - id: route_mode
                type: switch
                parameters:
                  on: "${steps.capture_request.json.submit}"
                  branch.true: done
                  branch._default: done
                branches:
                  "true": done
                  _default: done
              - id: done
                type: assign
                parameters:
                  target: final
                  value: "$input"
            """);

        WorkflowRunInputContract.RequiresJsonInput(definition).Should().BeTrue();
    }

    [Fact]
    public void RequiresJsonInput_WhenEntryStepIsBareTransform_ShouldBeFalse()
    {
        // A transform with no raw-input capture upstream makes no demonstrable
        // claim about the run input (it may render a literal template); gating
        // it would reject legitimate free-text runs at start.
        var definition = Parse(
            """
            name: probe_bare_transform
            roles: []
            steps:
              - id: only-step
                type: transform
            """);

        WorkflowRunInputContract.RequiresJsonInput(definition).Should().BeFalse();
    }

    [Fact]
    public void RequiresJsonInput_WhenInputFeedsLlmPrompt_ShouldBeFalse()
    {
        // Free-text workflows keep their semantics: no capture->transform, no
        // .json accessor on a raw-input capture.
        var definition = Parse(
            """
            name: probe_llm_first
            roles:
              - id: writer
                name: Writer
                system_prompt: Summarize the request.
            steps:
              - id: summarize
                type: llm_call
                target_role: writer
                parameters:
                  prompt_prefix: "Summarize:"
            """);

        WorkflowRunInputContract.RequiresJsonInput(definition).Should().BeFalse();
    }

    [Fact]
    public void RequiresJsonInput_WhenCaptureIsEchoOnly_ShouldBeFalse()
    {
        var definition = Parse(
            """
            name: probe_echo_only
            steps:
              - id: capture_request
                type: assign
                parameters:
                  target: raw_request
                  value: "$input"
              - id: done
                type: assign
                parameters:
                  target: final
                  value: "done"
            """);

        WorkflowRunInputContract.RequiresJsonInput(definition).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"period_label":"2026年8月","submit":false}""", true)]
    [InlineData("""{"days_left":"3"}""", true)]
    [InlineData("[1,2,3]", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("帮我预览2026年8月的状态提醒，先不要发送。", false)]
    [InlineData("{broken json", false)]
    public void IsBoundedJson_MirrorsRendererAcceptance(string input, bool expected)
    {
        WorkflowRunInputContract.IsBoundedJson(input).Should().Be(expected);
    }

    [Fact]
    public void BuildViolationMessage_IsCorrectiveAndDoesNotEchoInput()
    {
        var message = WorkflowRunInputContract.BuildViolationMessage(
            "workflow-alpha",
            "帮我预览2026年8月的状态提醒");

        message.Should().Contain("workflow-alpha");
        message.Should().Contain("serialized JSON");
        message.Should().Contain("inputs.prompt");
        message.Should().NotContain("状态提醒");
    }

    private static WorkflowDefinition Parse(string yaml) => new WorkflowParser().Parse(yaml);
}
