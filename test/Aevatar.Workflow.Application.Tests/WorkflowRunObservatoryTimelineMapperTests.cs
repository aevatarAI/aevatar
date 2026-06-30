using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Observatory;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

// Test-add (06-19-workflow-run-observatory / C2, spec §7): the timeline mapper is the pure stage -> AGUI
// view-event projection over a committed run-report timeline. It must map every known stage to its AGUI
// Kind (unknown -> "Event"), carry the role-reply Content only as supplied, build a ToolCall detail solely
// for the "tool.call" stage by reading the committed data map (success/arguments/result/error), and tolerate
// null Stage/Message/Data without leaking nulls into the view DTO. ToUsageTotals copies the report's
// authoritative per-run usage. Both entry points throw on a null argument.
public sealed class WorkflowRunObservatoryTimelineMapperTests
{
    [Theory]
    [InlineData("workflow.start", "RunStarted")]
    [InlineData("step.request", "StepStarted")]
    [InlineData("role.reply", "Message")]
    [InlineData("tool.call", "ToolCall")]
    [InlineData("step.completed", "StepFinished")]
    [InlineData("step.failed", "RunError")]
    [InlineData("workflow.failed", "RunError")]
    [InlineData("workflow.suspended", "HumanInputRequest")]
    [InlineData("signal.waiting", "SignalWaiting")]
    [InlineData("signal.buffered", "SignalBuffered")]
    [InlineData("workflow.completed", "RunFinished")]
    [InlineData("workflow.stopped", "RunStopped")]
    public void ToViewEvent_ShouldMapEveryKnownStageToItsAguiKind(string stage, string expectedKind)
    {
        var item = new WorkflowRunTimelineEvent { Stage = stage };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item);

        view.Kind.Should().Be(expectedKind);
        view.Stage.Should().Be(stage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown.stage")]
    [InlineData("role.failed")]
    [InlineData("TOOL.CALL")]
    public void ToViewEvent_WhenStageIsUnrecognized_ShouldMapToGenericEventKind(string stage)
    {
        var item = new WorkflowRunTimelineEvent { Stage = stage };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item);

        view.Kind.Should().Be("Event");
    }

    [Fact]
    public void ToViewEvent_ShouldCopyAllScalarTimelineFieldsAndTheSuppliedReplyContent()
    {
        var timestamp = DateTimeOffset.Parse("2026-06-19T10:11:12Z");
        var item = new WorkflowRunTimelineEvent
        {
            Stage = "step.request",
            Timestamp = timestamp,
            Message = "writer step",
            AgentId = "agent-7",
            StepId = "step-3",
            StepType = "role-call",
            Data = new Dictionary<string, string> { ["model"] = "gpt-x" },
        };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item, "the role reply text");

        view.Kind.Should().Be("StepStarted");
        view.TimestampUtc.Should().Be(timestamp);
        view.Stage.Should().Be("step.request");
        view.Message.Should().Be("writer step");
        view.AgentId.Should().Be("agent-7");
        view.StepId.Should().Be("step-3");
        view.StepType.Should().Be("role-call");
        view.Content.Should().Be("the role reply text");
        view.Data.Should().Contain(new KeyValuePair<string, string>("model", "gpt-x"));
        // ToolCall detail is only built for the tool.call stage.
        view.ToolCall.Should().BeNull();
    }

    [Fact]
    public void ToViewEvent_WhenStageIsNotRoleReply_ShouldStillCarryWhateverContentWasSupplied()
    {
        // The mapper does not gate Content on stage; it copies the argument verbatim (empty default).
        var item = new WorkflowRunTimelineEvent { Stage = "workflow.start" };

        WorkflowRunObservatoryTimelineMapper.ToViewEvent(item).Content.Should().BeEmpty();
        WorkflowRunObservatoryTimelineMapper.ToViewEvent(item, "spill").Content.Should().Be("spill");
    }

    [Fact]
    public void ToViewEvent_WhenRoleReplyContentIsNull_ShouldNormalizeToEmptyString()
    {
        var item = new WorkflowRunTimelineEvent { Stage = "role.reply" };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item, roleReplyContent: null!);

        view.Content.Should().BeEmpty();
    }

    [Fact]
    public void ToViewEvent_WhenStageIsNull_ShouldFallBackToEmptyStageAndGenericKind()
    {
        var item = new WorkflowRunTimelineEvent { Stage = null! };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item);

        view.Stage.Should().BeEmpty();
        view.Kind.Should().Be("Event");
        // Empty stage is not "tool.call", so no detail is built.
        view.ToolCall.Should().BeNull();
    }

    [Fact]
    public void ToViewEvent_WhenScalarFieldsAreNull_ShouldNormalizeThemToEmptyStrings()
    {
        var item = new WorkflowRunTimelineEvent
        {
            Stage = "step.request",
            Message = null!,
            AgentId = null!,
            StepId = null!,
            StepType = null!,
        };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item);

        view.Message.Should().BeEmpty();
        view.AgentId.Should().BeEmpty();
        view.StepId.Should().BeEmpty();
        view.StepType.Should().BeEmpty();
    }

    [Fact]
    public void ToViewEvent_WhenDataIsNull_ShouldSubstituteAnEmptyOrdinalDictionary()
    {
        var item = new WorkflowRunTimelineEvent { Stage = "workflow.start", Data = null! };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item);

        view.Data.Should().NotBeNull();
        view.Data.Should().BeEmpty();
    }

    [Fact]
    public void ToViewEvent_WhenStageIsToolCall_ShouldBuildToolCallDetailFromTheDataMap()
    {
        var item = new WorkflowRunTimelineEvent
        {
            Stage = "tool.call",
            Message = "code_execute",
            Data = new Dictionary<string, string>
            {
                ["call_id"] = "call-42",
                ["arguments_json"] = """{"language":"python"}""",
                ["result_json"] = """{"output":"ok"}""",
                ["success"] = "true",
                ["error"] = "",
            },
        };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item);

        view.Kind.Should().Be("ToolCall");
        view.ToolCall.Should().NotBeNull();
        view.ToolCall!.ToolName.Should().Be("code_execute");
        view.ToolCall.CallId.Should().Be("call-42");
        view.ToolCall.ArgumentsJson.Should().Be("""{"language":"python"}""");
        view.ToolCall.ResultJson.Should().Be("""{"output":"ok"}""");
        view.ToolCall.Success.Should().BeTrue();
        view.ToolCall.Error.Should().BeEmpty();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    public void ToViewEvent_ToolCallSuccess_ShouldBeCaseInsensitiveEqualityAgainstTrue(string raw, bool expected)
    {
        var item = new WorkflowRunTimelineEvent
        {
            Stage = "tool.call",
            Message = "tool",
            Data = new Dictionary<string, string> { ["success"] = raw },
        };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item);

        view.ToolCall!.Success.Should().Be(expected);
    }

    [Fact]
    public void ToViewEvent_WhenToolCallFailed_ShouldSurfaceTheErrorAndFalseSuccess()
    {
        var item = new WorkflowRunTimelineEvent
        {
            Stage = "tool.call",
            Message = "code_execute",
            Data = new Dictionary<string, string>
            {
                ["success"] = "false",
                ["error"] = "sandbox timeout",
            },
        };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item);

        view.ToolCall!.Success.Should().BeFalse();
        view.ToolCall.Error.Should().Be("sandbox timeout");
    }

    [Fact]
    public void ToViewEvent_WhenToolCallDataKeysAreMissing_ShouldDefaultEveryDetailFieldToEmpty()
    {
        var item = new WorkflowRunTimelineEvent
        {
            Stage = "tool.call",
            Message = "bare_tool",
            Data = new Dictionary<string, string>(),
        };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item);

        view.ToolCall.Should().NotBeNull();
        view.ToolCall!.ToolName.Should().Be("bare_tool");
        view.ToolCall.CallId.Should().BeEmpty();
        view.ToolCall.ArgumentsJson.Should().BeEmpty();
        view.ToolCall.ResultJson.Should().BeEmpty();
        view.ToolCall.Success.Should().BeFalse();
        view.ToolCall.Error.Should().BeEmpty();
    }

    [Fact]
    public void ToViewEvent_WhenToolCallDataIsNull_ShouldStillBuildAnEmptyDetailWithoutThrowing()
    {
        var item = new WorkflowRunTimelineEvent
        {
            Stage = "tool.call",
            Message = null!,
            Data = null!,
        };

        var view = WorkflowRunObservatoryTimelineMapper.ToViewEvent(item);

        view.ToolCall.Should().NotBeNull();
        view.ToolCall!.ToolName.Should().BeEmpty();
        view.ToolCall.CallId.Should().BeEmpty();
        view.ToolCall.Success.Should().BeFalse();
    }

    [Fact]
    public void ToViewEvent_WhenItemIsNull_ShouldThrowArgumentNullException()
    {
        var act = () => WorkflowRunObservatoryTimelineMapper.ToViewEvent(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("item");
    }

    [Fact]
    public void ToUsageTotals_ShouldCopyTheAuthoritativePerRunUsageFigures()
    {
        var usage = new WorkflowRunUsageMetrics
        {
            PromptTokens = 120,
            CompletionTokens = 45,
            TotalTokens = 165,
            Cost = 0.0031,
            // Model / LatencyMs are not part of the totals projection.
            Model = "gpt-x",
            LatencyMs = 999,
        };

        var totals = WorkflowRunObservatoryTimelineMapper.ToUsageTotals(usage);

        totals.PromptTokens.Should().Be(120);
        totals.CompletionTokens.Should().Be(45);
        totals.TotalTokens.Should().Be(165);
        totals.Cost.Should().Be(0.0031);
    }

    [Fact]
    public void ToUsageTotals_WhenUsageIsZero_ShouldProduceZeroedTotals()
    {
        var totals = WorkflowRunObservatoryTimelineMapper.ToUsageTotals(new WorkflowRunUsageMetrics());

        totals.PromptTokens.Should().Be(0);
        totals.CompletionTokens.Should().Be(0);
        totals.TotalTokens.Should().Be(0);
        totals.Cost.Should().Be(0d);
    }

    [Fact]
    public void ToUsageTotals_WhenUsageIsNull_ShouldThrowArgumentNullException()
    {
        var act = () => WorkflowRunObservatoryTimelineMapper.ToUsageTotals(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("usage");
    }
}
