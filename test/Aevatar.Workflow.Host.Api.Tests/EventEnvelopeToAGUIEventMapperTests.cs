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
        Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Children),
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
    public void StartWorkflowEvent_WhenPublisherMissing_ShouldFallbackToWorkflowName()
    {
        var envelope = Wrap(new StartWorkflowEvent
        {
            WorkflowName = "fallback-workflow",
            Input = "hello",
        });
        envelope.Route = null;

        var events = CreateMapper().Map(envelope);

        events.Should().ContainSingle();
        events[0].RunStarted.ThreadId.Should().Be("fallback-workflow");
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
    public void StepCompletedEvent_ShouldMapStepFinishedAndCustomPayload()
    {
        var events = CreateMapper().Map(Wrap(new StepCompletedEvent
        {
            RunId = "run-2",
            StepId = "evaluate",
            Success = true,
            Output = "approved",
            Error = "",
            NextStepId = "archive",
            BranchKey = "pass",
            AssignedVariable = "decision",
            AssignedValue = "approved",
            Annotations =
            {
                ["reason"] = "score-high",
            },
        }));

        events.Should().HaveCount(2);
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.StepFinished);
        events[0].StepFinished.StepName.Should().Be("evaluate");
        events[1].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.Custom);
        events[1].Custom.Name.Should().Be("aevatar.step.completed");
        var payload = events[1].Custom.Payload.Unpack<WorkflowStepCompletedCustomPayload>();
        payload.RunId.Should().Be("run-2");
        payload.StepId.Should().Be("evaluate");
        payload.Success.Should().BeTrue();
        payload.NextStepId.Should().Be("archive");
        payload.BranchKey.Should().Be("pass");
        payload.AssignedVariable.Should().Be("decision");
        payload.AssignedValue.Should().Be("approved");
        payload.Annotations.Should().ContainKey("reason").WhoseValue.Should().Be("score-high");
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
    public void TextStreamEvents_ShouldMapStartContentAndEnd_WithEnvelopeIdFallback()
    {
        var mapper = CreateMapper();
        var startEnvelope = Wrap(new TextMessageStartEvent());
        var contentEnvelope = Wrap(new TextMessageContentEvent
        {
            Delta = "chunk",
        });
        var endEnvelope = Wrap(new TextMessageEndEvent());

        var startEvents = mapper.Map(startEnvelope);
        var contentEvents = mapper.Map(contentEnvelope);
        var endEvents = mapper.Map(endEnvelope);

        startEvents.Should().ContainSingle();
        startEvents[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.TextMessageStart);
        startEvents[0].TextMessageStart.MessageId.Should().Be($"msg:{startEnvelope.Id}");
        startEvents[0].TextMessageStart.Role.Should().Be("assistant");

        contentEvents.Should().ContainSingle();
        contentEvents[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.TextMessageContent);
        contentEvents[0].TextMessageContent.MessageId.Should().Be($"msg:{contentEnvelope.Id}");
        contentEvents[0].TextMessageContent.Delta.Should().Be("chunk");

        endEvents.Should().ContainSingle();
        endEvents[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.TextMessageEnd);
        endEvents[0].TextMessageEnd.MessageId.Should().Be($"msg:{endEnvelope.Id}");
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
    public void TextMessageReasoningEvent_WhenPublisherMissing_ShouldFallbackToAssistant()
    {
        var envelope = Wrap(new TextMessageReasoningEvent
        {
            SessionId = "reasoning-session",
            Delta = "thinking chunk",
        });
        envelope.Route = null;

        var events = CreateMapper().Map(envelope);

        events.Should().ContainSingle();
        events[0].Custom.Payload.Unpack<WorkflowReasoningCustomPayload>().Role.Should().Be("assistant");
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
    public void WaitingForSignalEvent_WhenRunIdMissing_ShouldFallbackToCorrelationId()
    {
        var envelope = Wrap(new WaitingForSignalEvent
        {
            StepId = "wait_gate",
            SignalName = "ops_window_open",
        });
        envelope.Propagation = new EnvelopePropagation
        {
            CorrelationId = "corr-wait",
        };

        var events = CreateMapper().Map(envelope);

        events.Should().ContainSingle();
        events[0].Custom.Payload.Unpack<WorkflowWaitingSignalCustomPayload>().RunId.Should().Be("corr-wait");
    }

    [Fact]
    public void ToolCallAndResultEvents_ShouldMapToToolLifecycle()
    {
        var mapper = CreateMapper();
        var startEvents = mapper.Map(Wrap(new ToolCallEvent
        {
            CallId = "call-1",
            ToolName = "search",
        }));
        var endEvents = mapper.Map(Wrap(new ToolResultEvent
        {
            CallId = "call-1",
            ResultJson = "{\"ok\":true}",
        }));

        startEvents.Should().ContainSingle();
        startEvents[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.ToolCallStart);
        startEvents[0].ToolCallStart.ToolCallId.Should().Be("call-1");
        startEvents[0].ToolCallStart.ToolName.Should().Be("search");

        endEvents.Should().ContainSingle();
        endEvents[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.ToolCallEnd);
        endEvents[0].ToolCallEnd.ToolCallId.Should().Be("call-1");
        endEvents[0].ToolCallEnd.Result.Should().Be("{\"ok\":true}");
    }

    [Fact]
    public void IndividualHandlers_ShouldReturnFalse_ForUnsupportedEnvelopes()
    {
        var unsupported = Wrap(new ParentChangedEvent
        {
            OldParent = "a",
            NewParent = "b",
        });
        var noPayload = new EventEnvelope
        {
            Id = "no-payload",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        new StartWorkflowRunEventEnvelopeMappingHandler().TryMap(unsupported, out var startEvents).Should().BeFalse();
        startEvents.Should().BeEmpty();

        new StepRequestRunEventEnvelopeMappingHandler().TryMap(unsupported, out var requestEvents).Should().BeFalse();
        requestEvents.Should().BeEmpty();

        new StepCompletedRunEventEnvelopeMappingHandler().TryMap(unsupported, out var completedEvents).Should().BeFalse();
        completedEvents.Should().BeEmpty();

        new AITextStreamRunEventEnvelopeMappingHandler().TryMap(noPayload, out var textEvents).Should().BeFalse();
        textEvents.Should().BeEmpty();

        new AIReasoningRunEventEnvelopeMappingHandler().TryMap(unsupported, out var reasoningEvents).Should().BeFalse();
        reasoningEvents.Should().BeEmpty();

        new WorkflowCompletedRunEventEnvelopeMappingHandler().TryMap(unsupported, out var workflowCompletedEvents).Should().BeFalse();
        workflowCompletedEvents.Should().BeEmpty();

        new ToolCallRunEventEnvelopeMappingHandler().TryMap(noPayload, out var toolEvents).Should().BeFalse();
        toolEvents.Should().BeEmpty();

        new WorkflowSuspendedRunEventEnvelopeMappingHandler().TryMap(unsupported, out var suspendedEvents).Should().BeFalse();
        suspendedEvents.Should().BeEmpty();

        new WorkflowWaitingSignalRunEventEnvelopeMappingHandler().TryMap(unsupported, out var waitingEvents).Should().BeFalse();
        waitingEvents.Should().BeEmpty();
    }

    [Fact]
    public void PublicMappingPaths_ShouldCoverHelperFallbackBranches()
    {
        var startEnvelope = new EventEnvelope
        {
            Id = "start-no-ts",
            Payload = Any.Pack(new StartWorkflowEvent
            {
                WorkflowName = "fallback-thread",
            }),
        };
        var startEvents = CreateMapper().Map(startEnvelope);
        startEvents.Should().ContainSingle();
        startEvents[0].Timestamp.Should().BeNull();
        startEvents[0].RunStarted.ThreadId.Should().Be("fallback-thread");

        var waitingEnvelope = new EventEnvelope
        {
            Id = "wait-no-corr",
            Payload = Any.Pack(new WaitingForSignalEvent
            {
                StepId = "wait_gate",
                SignalName = "ops_window_open",
            }),
        };
        var waitingEvents = CreateMapper().Map(waitingEnvelope);
        waitingEvents.Should().ContainSingle();
        waitingEvents[0].Custom.Payload.Unpack<WorkflowWaitingSignalCustomPayload>().RunId.Should().BeEmpty();

        var reasoningEnvelope = Wrap(new TextMessageReasoningEvent
        {
            SessionId = "reasoning-session",
            Delta = "thinking chunk",
        });
        reasoningEnvelope.EnsureRoute().PublisherActorId = " reviewer ";
        var reasoningEvents = CreateMapper().Map(reasoningEnvelope);
        reasoningEvents.Should().ContainSingle();
        reasoningEvents[0].Custom.Payload.Unpack<WorkflowReasoningCustomPayload>().Role.Should().Be("reviewer");
    }

    [Fact]
    public void UnknownOrNullPayload_ShouldReturnEmpty()
    {
        var unknown = CreateMapper().Map(Wrap(new ParentChangedEvent { OldParent = "a", NewParent = "b" }));
        var nullPayload = CreateMapper().Map(new EventEnvelope
        {
            Id = "test",
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("x", TopologyAudience.Children),
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
