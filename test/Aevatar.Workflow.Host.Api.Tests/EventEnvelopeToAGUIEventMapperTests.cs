using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class EventEnvelopeToAGUIEventMapperTests
{
    private static EventEnvelope WrapObserved<T>(T evt) where T : IMessage => new()
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

    private static EventEnvelope WrapCommitted<T>(
        T evt,
        long version = 1,
        bool includeEnvelopeTimestamp = true,
        bool includeStateEventTimestamp = true)
        where T : IMessage
    {
        var eventId = Guid.NewGuid().ToString("N");
        var envelopeTimestamp = includeEnvelopeTimestamp
            ? Timestamp.FromDateTime(DateTime.UtcNow)
            : null;
        var stateEventTimestamp = includeStateEventTimestamp
            ? envelopeTimestamp?.Clone()
            : null;

        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = envelopeTimestamp,
            Route = EnvelopeRouteSemantics.CreateObserverPublication("test"),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "cmd-1",
            },
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = stateEventTimestamp,
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new WorkflowRunState()),
            }),
        };
    }

    [Fact]
    public void StartWorkflowEvent_ShouldMapToRunStartedEnvelope()
    {
        var events = CreateMapper().Map(WrapCommitted(new StartWorkflowEvent
        {
            WorkflowName = "review",
            Input = "hello",
            RunId = "run-1",
        }));

        events.Should().ContainSingle();
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunStarted);
        events[0].RunStarted.ThreadId.Should().Be("test");
        events[0].RunStarted.RunId.Should().Be("run-1");
    }

    [Fact]
    public void StartWorkflowEvent_WhenPublisherMissing_ShouldFallbackToWorkflowName()
    {
        var envelope = WrapCommitted(new StartWorkflowEvent
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
    public void WorkflowRunExecutionStartedEvent_ShouldBeIgnored()
    {
        var events = CreateMapper().Map(WrapCommitted(new WorkflowRunExecutionStartedEvent
        {
            RunId = "run-1",
            WorkflowName = "review",
            Input = "hello",
            DefinitionActorId = "definition-actor-1",
        }));

        events.Should().BeEmpty();
    }

    [Fact]
    public void StepRequestEvent_ShouldMapToStepStartedAndCustomPayload()
    {
        var events = CreateMapper().Map(WrapCommitted(new StepRequestEvent
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
    public void StepCompletedEvent_ShouldMapStepFinishedAndExposeTypedFields()
    {
        var events = CreateMapper().Map(WrapCommitted(new StepCompletedEvent
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
                ["source"] = "tests",
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
        payload.Annotations.Should().ContainKey("source").WhoseValue.Should().Be("tests");
    }

    [Fact]
    public void ChatResponseEvent_ShouldMapToFullTextSequence()
    {
        var envelope = WrapCommitted(new ChatResponseEvent
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
        var startEnvelope = WrapCommitted(new TextMessageStartEvent());
        var contentEnvelope = WrapCommitted(new TextMessageContentEvent
        {
            Delta = "chunk",
        });
        var endEnvelope = WrapCommitted(new TextMessageEndEvent());

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
        var envelope = WrapCommitted(new TextMessageReasoningEvent
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
        var envelope = WrapCommitted(new TextMessageReasoningEvent
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
        var success = CreateMapper().Map(WrapCommitted(new WorkflowCompletedEvent
        {
            WorkflowName = "review",
            Success = true,
            Output = "完成",
        }));
        var failure = CreateMapper().Map(WrapCommitted(new WorkflowCompletedEvent
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
    public void WorkflowStoppedEvents_ShouldMapToRunStoppedEnvelope()
    {
        var stopped = CreateMapper().Map(WrapCommitted(new WorkflowStoppedEvent
        {
            WorkflowName = "review",
            RunId = "run-stop-1",
            Reason = "manual",
        }));
        var runStopped = CreateMapper().Map(WrapCommitted(new WorkflowRunStoppedEvent
        {
            RunId = "run-stop-2",
            Reason = "forced",
        }));

        stopped.Should().ContainSingle();
        stopped[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunStopped);
        stopped[0].RunStopped.RunId.Should().Be("run-stop-1");
        stopped[0].RunStopped.Reason.Should().Be("manual");

        runStopped.Should().ContainSingle();
        runStopped[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunStopped);
        runStopped[0].RunStopped.RunId.Should().Be("run-stop-2");
        runStopped[0].RunStopped.Reason.Should().Be("forced");
    }

    [Fact]
    public void WorkflowSuspendedAndWaitingSignal_ShouldMapToCustomPayloads()
    {
        var suspended = CreateMapper().Map(WrapCommitted(new WorkflowSuspendedEvent
        {
            RunId = "run-1",
            StepId = "get_context",
            SuspensionType = WorkflowSuspensionType.HumanInput,
            Prompt = "请提供补充信息",
            Content = "已有上下文",
            TimeoutSeconds = 1800,
            VariableName = "user_context",
            DeliveryTargetId = "agent-delivery-1",
        }));
        var waiting = CreateMapper().Map(WrapCommitted(new WaitingForSignalEvent
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
        request.Content.Should().Be("已有上下文");
        request.DeliveryTargetId.Should().Be("agent-delivery-1");
        request.Metadata.Should().NotContainKey("variable");

        waiting.Should().ContainSingle();
        waiting[0].Custom.Name.Should().Be("aevatar.workflow.waiting_signal");
        waiting[0].Custom.Payload.Unpack<WorkflowWaitingSignalCustomPayload>().RunId.Should().Be("run-expected");
    }

    [Fact]
    public void WaitingForSignalEvent_WhenRunIdMissing_ShouldFallbackToCorrelationId()
    {
        var envelope = WrapCommitted(new WaitingForSignalEvent
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
    public void WorkflowSignalBufferedEvent_ProjectsTo_CustomBufferedSignalEvent()
    {
        var envelope = WrapCommitted(new WorkflowSignalBufferedEvent
        {
            RunId = "run-b",
            StepId = "wait-b",
            SignalName = "reply_ready",
            Payload = "payload",
            ReceivedAtUnixTimeMs = 123,
        });

        var events = CreateMapper().Map(envelope);

        events.Should().ContainSingle();
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.Custom);
        events[0].Custom.Name.Should().Be("aevatar.workflow.signal.buffered");
        var payload = events[0].Custom.Payload.Unpack<WorkflowSignalBufferedCustomPayload>();
        payload.RunId.Should().Be("run-b");
        payload.StepId.Should().Be("wait-b");
        payload.SignalName.Should().Be("reply_ready");
        payload.Payload.Should().Be("payload");
        payload.ReceivedAtUnixTimeMs.Should().Be(123);
    }

    [Fact]
    public void ToolCallAndResultEvents_ShouldMapToToolLifecycle()
    {
        var mapper = CreateMapper();
        var startEvents = mapper.Map(WrapCommitted(new ToolCallEvent
        {
            CallId = "call-1",
            ToolName = "search",
        }));
        var endEvents = mapper.Map(WrapCommitted(new ToolResultEvent
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
        var unsupported = WrapObserved(new ParentChangedEvent
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

        new WorkflowStoppedRunEventEnvelopeMappingHandler().TryMap(noPayload, out var workflowStoppedEvents).Should().BeFalse();
        workflowStoppedEvents.Should().BeEmpty();

        new ToolCallRunEventEnvelopeMappingHandler().TryMap(noPayload, out var toolEvents).Should().BeFalse();
        toolEvents.Should().BeEmpty();

        new WorkflowSuspendedRunEventEnvelopeMappingHandler().TryMap(unsupported, out var suspendedEvents).Should().BeFalse();
        suspendedEvents.Should().BeEmpty();

        new WorkflowWaitingSignalRunEventEnvelopeMappingHandler().TryMap(unsupported, out var waitingEvents).Should().BeFalse();
        waitingEvents.Should().BeEmpty();

        new WorkflowSignalBufferedRunEventEnvelopeMappingHandler().TryMap(unsupported, out var bufferedEvents).Should().BeFalse();
        bufferedEvents.Should().BeEmpty();
    }

    [Fact]
    public void PublicMappingPaths_ShouldCoverHelperFallbackBranches()
    {
        var startEnvelope = new EventEnvelope
        {
            Id = "start-no-ts",
            Payload = WrapCommitted(
                new StartWorkflowEvent
                {
                    WorkflowName = "fallback-thread",
                },
                includeEnvelopeTimestamp: false,
                includeStateEventTimestamp: false).Payload,
        };
        var startEvents = CreateMapper().Map(startEnvelope);
        startEvents.Should().ContainSingle();
        startEvents[0].Timestamp.Should().BeNull();
        startEvents[0].RunStarted.ThreadId.Should().Be("fallback-thread");

        var waitingEnvelope = new EventEnvelope
        {
            Id = "wait-no-corr",
            Payload = WrapCommitted(
                new WaitingForSignalEvent
                {
                    StepId = "wait_gate",
                    SignalName = "ops_window_open",
                },
                includeEnvelopeTimestamp: false,
                includeStateEventTimestamp: false).Payload,
        };
        var waitingEvents = CreateMapper().Map(waitingEnvelope);
        waitingEvents.Should().ContainSingle();
        waitingEvents[0].Custom.Payload.Unpack<WorkflowWaitingSignalCustomPayload>().RunId.Should().BeEmpty();

        var reasoningEnvelope = WrapCommitted(new TextMessageReasoningEvent
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
    public void UnknownPayload_ShouldFallbackToRawObservedCustomEvent()
    {
        var unknownEnvelope = WrapCommitted(new ParentChangedEvent { OldParent = "a", NewParent = "b" }, version: 7);

        var unknown = CreateMapper().Map(unknownEnvelope);

        unknown.Should().ContainSingle();
        unknown[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.Custom);
        unknown[0].Custom.Name.Should().Be("aevatar.raw.observed");
        var payload = unknown[0].Custom.Payload.Unpack<WorkflowObservedEnvelopeCustomPayload>();
        payload.EventId.Should().Be(unknownEnvelope.Id);
        payload.CorrelationId.Should().Be("cmd-1");
        payload.StateVersion.Should().Be(7);
        payload.PayloadTypeUrl.Should().Contain(nameof(ParentChangedEvent));
        payload.Payload.Unpack<ParentChangedEvent>().OldParent.Should().Be("a");
    }

    [Fact]
    public void NullPayload_ShouldReturnEmpty()
    {
        var nullPayload = CreateMapper().Map(new EventEnvelope
        {
            Id = "test",
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("x", TopologyAudience.Children),
        });

        nullPayload.Should().BeEmpty();
    }

    private static IEventEnvelopeToWorkflowRunEventMapper CreateMapper()
    {
        return new EventEnvelopeToWorkflowRunEventMapper(
        [
            new WorkflowRunExecutionStartedEnvelopeMappingHandler(),
            new StartWorkflowRunEventEnvelopeMappingHandler(),
            new StepRequestRunEventEnvelopeMappingHandler(),
            new StepCompletedRunEventEnvelopeMappingHandler(),
            new AITextStreamRunEventEnvelopeMappingHandler(),
            new AIReasoningRunEventEnvelopeMappingHandler(),
            new WorkflowCompletedRunEventEnvelopeMappingHandler(),
            new WorkflowStoppedRunEventEnvelopeMappingHandler(),
            new ToolCallRunEventEnvelopeMappingHandler(),
            new WorkflowSuspendedRunEventEnvelopeMappingHandler(),
            new WorkflowWaitingSignalRunEventEnvelopeMappingHandler(),
            new WorkflowSignalBufferedRunEventEnvelopeMappingHandler(),
        ]);
    }
}
