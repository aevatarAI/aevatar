using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class EventEnvelopeToAGUIEventMapperTests
{
    private static EventEnvelope Wrap<T>(T evt) where T : IMessage => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        Route = new EnvelopeRoute
        {
            PublisherActorId = "test",
            Direction = EventDirection.Down,
        },
        Propagation = new EnvelopePropagation
        {
            CorrelationId = "cmd-1",
        },
    };

    [Fact]
    public void StartWorkflowEvent_ShouldMapToRunStartedEnvelope()
    {
        var events = CreateMapper().Map(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "review",
            Input = "hello",
        }));

        events.Should().ContainSingle();
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunStarted);
        events[0].RunStarted.ThreadId.Should().Be("test");
    }

    [Fact]
    public void StepRequestEvent_ShouldMapToStepStartedAndCustomPayload()
    {
        var events = CreateMapper().Map(Wrap(new StepRequestEvent
        {
            RunId = "run-1",
            StepId = "analyze",
            StepType = "llm_call",
            TargetRole = "assistant",
            Input = "hello",
        }));

        events.Should().HaveCount(2);
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.StepStarted);
        events[0].StepStarted.StepName.Should().Be("analyze");
        events[1].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.Custom);
        events[1].Custom.Name.Should().Be("aevatar.step.request");
        var payload = events[1].Custom.Payload.Unpack<WorkflowStepRequestCustomPayload>();
        payload.RunId.Should().Be("run-1");
        payload.TargetRole.Should().Be("assistant");
    }

    [Fact]
    public void ChatResponseEvent_ShouldMapToFullTextSequence()
    {
        var envelope = Wrap(new ChatResponseEvent
        {
            Content = "分析结果如下...",
            SessionId = "session-1",
        });

        var events = CreateMapper().Map(envelope);

        events.Should().HaveCount(3);
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.TextMessageStart);
        events[1].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.TextMessageContent);
        events[2].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.TextMessageEnd);
        events[1].TextMessageContent.Delta.Should().Be("分析结果如下...");
        events[1].TextMessageContent.MessageId.Should().Be("msg:session-1");
    }

    [Fact]
    public void TextMessageReasoningEvent_ShouldMapToCustomReasoningPayload()
    {
        var envelope = Wrap(new TextMessageReasoningEvent
        {
            SessionId = "reasoning-session",
            Delta = "thinking chunk",
        });
        envelope.EnsureRoute().PublisherActorId = "wf:planner";

        var events = CreateMapper().Map(envelope);

        events.Should().ContainSingle();
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.Custom);
        events[0].Custom.Name.Should().Be("aevatar.llm.reasoning");
        var payload = events[0].Custom.Payload.Unpack<WorkflowReasoningCustomPayload>();
        payload.SessionId.Should().Be("reasoning-session");
        payload.Delta.Should().Be("thinking chunk");
        payload.Role.Should().Be("planner");
    }

    [Fact]
    public void WorkflowCompletedEvent_ShouldMapSuccessAndFailureCases()
    {
        var success = CreateMapper().Map(Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "review",
            Success = true,
            Output = "完成",
        }));
        var failure = CreateMapper().Map(Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "review",
            Success = false,
            Error = "超时",
        }));

        success.Should().ContainSingle();
        success[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunFinished);
        success[0].RunFinished.ThreadId.Should().Be("test");
        success[0].RunFinished.Result.Unpack<WorkflowRunResultPayload>().Output.Should().Be("完成");

        failure.Should().ContainSingle();
        failure[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunError);
        failure[0].RunError.Message.Should().Be("超时");
        failure[0].RunError.Code.Should().Be("WORKFLOW_FAILED");
    }

    [Fact]
    public void WorkflowSuspendedAndWaitingSignal_ShouldMapToCustomPayloads()
    {
        var suspended = CreateMapper().Map(Wrap(new WorkflowSuspendedEvent
        {
            RunId = "run-1",
            StepId = "get_context",
            SuspensionType = "human_input",
            Prompt = "请提供补充信息",
            TimeoutSeconds = 1800,
            VariableName = "user_context",
        }));
        var waiting = CreateMapper().Map(Wrap(new WaitingForSignalEvent
        {
            RunId = "run-expected",
            StepId = "wait_gate",
            SignalName = "ops_window_open",
            Prompt = "waiting for ops window",
            TimeoutMs = 30000,
        }));

        suspended.Should().ContainSingle();
        suspended[0].Custom.Name.Should().Be("aevatar.human_input.request");
        var request = suspended[0].Custom.Payload.Unpack<WorkflowHumanInputRequestCustomPayload>();
        request.VariableName.Should().Be("user_context");

        waiting.Should().ContainSingle();
        waiting[0].Custom.Name.Should().Be("aevatar.workflow.waiting_signal");
        waiting[0].Custom.Payload.Unpack<WorkflowWaitingSignalCustomPayload>().RunId.Should().Be("run-expected");
    }

    [Fact]
    public void UnknownOrNullPayload_ShouldReturnEmpty()
    {
        var unknown = CreateMapper().Map(Wrap(new ParentChangedEvent { OldParent = "a", NewParent = "b" }));
        var nullPayload = CreateMapper().Map(new EventEnvelope
        {
            Id = "test",
            Route = new EnvelopeRoute
            {
                PublisherActorId = "x",
                Direction = EventDirection.Down,
            },
        });

        unknown.Should().BeEmpty();
        nullPayload.Should().BeEmpty();
    }

    private static IEventEnvelopeToWorkflowRunEventMapper CreateMapper()
    {
        return new EventEnvelopeToWorkflowRunEventMapper(
        [
            new StartWorkflowRunEventEnvelopeMappingHandler(),
            new StepRequestRunEventEnvelopeMappingHandler(),
            new StepCompletedRunEventEnvelopeMappingHandler(),
            new AITextStreamRunEventEnvelopeMappingHandler(),
            new AIReasoningRunEventEnvelopeMappingHandler(),
            new WorkflowCompletedRunEventEnvelopeMappingHandler(),
            new ToolCallRunEventEnvelopeMappingHandler(),
            new WorkflowSuspendedRunEventEnvelopeMappingHandler(),
            new WorkflowWaitingSignalRunEventEnvelopeMappingHandler(),
        ]);
    }
}
