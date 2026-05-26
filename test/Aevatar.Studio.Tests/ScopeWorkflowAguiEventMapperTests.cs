using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ScopeWorkflowAguiEventMapperTests
{
    [Fact]
    public void BuildRunContextEvent_ShouldMapReceipt()
    {
        var receipt = new WorkflowChatRunAcceptedReceipt("actor-1", "wf", "cmd-1", "corr-1");
        var evt = ScopeWorkflowAguiEventMapper.BuildRunContextEvent(receipt);
        evt.Custom.Should().NotBeNull();
        evt.Custom.Name.Should().Be("aevatar.run.context");
        evt.Timestamp.Should().BeGreaterThan(0);
    }

    [Fact]
    public void BuildRunErrorEvent_ShouldMapException()
    {
        var ex = new InvalidOperationException("boom");
        var evt = ScopeWorkflowAguiEventMapper.BuildRunErrorEvent(ex);
        evt.RunError.Should().NotBeNull();
        evt.RunError.Message.Should().Be("boom");
        evt.RunError.Code.Should().Be("EXECUTION_FAILED");
    }

    [Fact]
    public void TryMap_RunStarted_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            Timestamp = 100,
            RunStarted = new WorkflowRunStartedEventPayload { ThreadId = "t1", RunId = "r1" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.RunStarted.Should().NotBeNull();
        evt.RunStarted.RunId.Should().Be("r1");
        evt.RunStarted.ThreadId.Should().Be("t1");
        evt.Timestamp.Should().Be(100);
    }

    [Fact]
    public void TryMap_RunStarted_ShouldFallbackToThreadId_WhenRunIdEmpty()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            RunStarted = new WorkflowRunStartedEventPayload { ThreadId = "t1" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.RunStarted.RunId.Should().Be("t1");
    }

    [Fact]
    public void TryMap_RunFinished_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload { ThreadId = "t1" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.RunFinished.Should().NotBeNull();
        evt.RunFinished.Should().NotBeNull();
    }

    [Fact]
    public void TryMap_RunError_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            RunError = new WorkflowRunErrorEventPayload { Message = "err", Code = "E1" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.RunError.Message.Should().Be("err");
        evt.RunError.Code.Should().Be("E1");
    }

    [Fact]
    public void TryMap_RunStopped_ShouldMapAsCustom()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            RunStopped = new WorkflowRunStoppedEventPayload { Reason = "user" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.Custom.Should().NotBeNull();
        evt.Custom.Name.Should().Be("aevatar.run.stopped");
    }

    [Fact]
    public void TryMap_StepStarted_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            StepStarted = new WorkflowStepStartedEventPayload { StepName = "s1" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.StepStarted.StepName.Should().Be("s1");
    }

    [Fact]
    public void TryMap_StepFinished_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            StepFinished = new WorkflowStepFinishedEventPayload { StepName = "s1" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.StepFinished.StepName.Should().Be("s1");
    }

    [Fact]
    public void TryMap_TextMessageStart_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            TextMessageStart = new WorkflowTextMessageStartEventPayload { MessageId = "m1", Role = "assistant" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.TextMessageStart.MessageId.Should().Be("m1");
        evt.TextMessageStart.Role.Should().Be("assistant");
    }

    [Fact]
    public void TryMap_TextMessageContent_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            TextMessageContent = new WorkflowTextMessageContentEventPayload { MessageId = "m1", Delta = "hello" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.TextMessageContent.Delta.Should().Be("hello");
    }

    [Fact]
    public void TryMap_TextMessageEnd_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            TextMessageEnd = new WorkflowTextMessageEndEventPayload { MessageId = "m1" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.TextMessageEnd.MessageId.Should().Be("m1");
    }

    [Fact]
    public void TryMap_StateSnapshot_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            StateSnapshot = new WorkflowStateSnapshotEventPayload(),
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.StateSnapshot.Should().NotBeNull();
    }

    [Fact]
    public void TryMap_ToolCallStart_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            ToolCallStart = new WorkflowToolCallStartEventPayload { ToolCallId = "tc1", ToolName = "tool" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.ToolCallStart.ToolCallId.Should().Be("tc1");
        evt.ToolCallStart.ToolName.Should().Be("tool");
    }

    [Fact]
    public void TryMap_ToolCallEnd_ShouldMap()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            ToolCallEnd = new WorkflowToolCallEndEventPayload { ToolCallId = "tc1", Result = "done" },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.ToolCallEnd.ToolCallId.Should().Be("tc1");
    }

    [Fact]
    public void TryMap_CustomHumanInputRequest_ShouldMap()
    {
        var payload = new WorkflowHumanInputRequestCustomPayload
        {
            StepId = "step1", RunId = "run1", SuspensionType = "input",
            Prompt = "what?", TimeoutSeconds = 30, VariableName = "answer",
        };
        var frame = new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "human_input_request",
                Payload = Any.Pack(payload),
            },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.HumanInputRequest.Should().NotBeNull();
        evt.HumanInputRequest.StepId.Should().Be("step1");
        evt.HumanInputRequest.Prompt.Should().Be("what?");
    }

    [Fact]
    public void TryMap_CustomHumanInputResponse_ShouldMap()
    {
        var payload = new WorkflowHumanInputResponseCustomPayload
        {
            StepId = "step1", RunId = "run1", Approved = true, UserInput = "yes",
        };
        var frame = new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "human_input_response",
                Payload = Any.Pack(payload),
            },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.HumanInputResponse.Should().NotBeNull();
        evt.HumanInputResponse.Approved.Should().BeTrue();
    }

    [Fact]
    public void TryMap_CustomGeneric_ShouldPassThrough()
    {
        var frame = new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "my.event",
            },
        };
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeTrue();
        evt!.Custom.Name.Should().Be("my.event");
    }

    [Fact]
    public void TryMap_UnknownEventCase_ShouldReturnFalse()
    {
        var frame = new WorkflowRunEventEnvelope();
        ScopeWorkflowAguiEventMapper.TryMap(frame, out var evt).Should().BeFalse();
        evt.Should().BeNull();
    }
}
