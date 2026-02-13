// ─────────────────────────────────────────────────────────────
// AgUiProjector — EventEnvelope → AgUiEvent 投影
//
// 从 Actor Stream 上的 EventEnvelope 解包 payload，
// 映射为前端可消费的 AG-UI 事件。
// 纯函数，无副作用，每个 envelope 产生 0~N 个 AgUiEvent。
// ─────────────────────────────────────────────────────────────

using Aevatar.AGUI;
using Aevatar.AI;
using Aevatar.Cognitive;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Api.Projection;

/// <summary>
/// EventEnvelope payload → AG-UI 事件投影。
/// </summary>
public static class AgUiProjector
{
    /// <summary>
    /// 将一个 EventEnvelope 投影为 0~N 个 AG-UI 事件。
    /// </summary>
    public static IReadOnlyList<AgUiEvent> Project(EventEnvelope envelope)
    {
        if (envelope.Payload == null) return [];

        var ts = ToUnixMs(envelope.Timestamp);
        var payload = envelope.Payload;

        // ─── StartWorkflowEvent → RUN_STARTED ───
        if (payload.Is(StartWorkflowEvent.Descriptor))
        {
            var evt = payload.Unpack<StartWorkflowEvent>();
            return [new RunStartedEvent
            {
                Timestamp = ts,
                ThreadId = evt.WorkflowName,
                RunId = evt.RunId,
            }];
        }

        // ─── StepRequestEvent → STEP_STARTED + CUSTOM ───
        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var evt = payload.Unpack<StepRequestEvent>();
            return
            [
                new StepStartedEvent { Timestamp = ts, StepName = evt.StepId },
                new CustomEvent
                {
                    Timestamp = ts,
                    Name = "aevatar.step.request",
                    Value = new { evt.StepId, evt.StepType, evt.TargetRole, evt.RunId },
                },
            ];
        }

        // ─── StepCompletedEvent → STEP_FINISHED ───
        if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<StepCompletedEvent>();
            return [new StepFinishedEvent { Timestamp = ts, StepName = evt.StepId }];
        }

        // ─── AI TextMessageStartEvent → AGUI TEXT_MESSAGE_START ───
        if (payload.Is(AI.TextMessageStartEvent.Descriptor))
        {
            var evt = payload.Unpack<AI.TextMessageStartEvent>();
            var msgId = $"msg:{envelope.Id}";
            return [new AGUI.TextMessageStartEvent
            {
                Timestamp = ts, MessageId = msgId, Role = "assistant",
            }];
        }

        // ─── AI TextMessageContentEvent → AGUI TEXT_MESSAGE_CONTENT ───
        if (payload.Is(AI.TextMessageContentEvent.Descriptor))
        {
            var evt = payload.Unpack<AI.TextMessageContentEvent>();
            var msgId = $"msg:{envelope.Id}";
            return [new AGUI.TextMessageContentEvent
            {
                Timestamp = ts, MessageId = msgId, Delta = evt.Delta,
            }];
        }

        // ─── AI TextMessageEndEvent → AGUI TEXT_MESSAGE_END ───
        if (payload.Is(AI.TextMessageEndEvent.Descriptor))
        {
            var msgId = $"msg:{envelope.Id}";
            return [new AGUI.TextMessageEndEvent
            {
                Timestamp = ts, MessageId = msgId,
            }];
        }

        // ─── ChatResponseEvent → AGUI 完整消息（非流式兼容） ───
        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            var msgId = $"msg:{envelope.Id}";
            return
            [
                new AGUI.TextMessageStartEvent
                {
                    Timestamp = ts, MessageId = msgId, Role = "assistant",
                },
                new AGUI.TextMessageContentEvent
                {
                    Timestamp = ts, MessageId = msgId, Delta = evt.Content,
                },
                new AGUI.TextMessageEndEvent
                {
                    Timestamp = ts, MessageId = msgId,
                },
            ];
        }

        // ─── WorkflowCompletedEvent → RUN_FINISHED / RUN_ERROR ───
        if (payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<WorkflowCompletedEvent>();
            if (evt.Success)
                return [new RunFinishedEvent
                {
                    Timestamp = ts,
                    ThreadId = evt.WorkflowName,
                    RunId = evt.RunId,
                    Result = new { output = evt.Output },
                }];

            return [new RunErrorEvent
            {
                Timestamp = ts,
                Message = evt.Error,
                Code = "WORKFLOW_FAILED",
            }];
        }

        // ─── ToolCallEvent → TOOL_CALL_START ───
        if (payload.Is(ToolCallEvent.Descriptor))
        {
            var evt = payload.Unpack<ToolCallEvent>();
            return [new ToolCallStartEvent
            {
                Timestamp = ts, ToolCallId = evt.CallId, ToolName = evt.ToolName,
            }];
        }

        // ─── ToolResultEvent → TOOL_CALL_END ───
        if (payload.Is(ToolResultEvent.Descriptor))
        {
            var evt = payload.Unpack<ToolResultEvent>();
            return [new ToolCallEndEvent
            {
                Timestamp = ts, ToolCallId = evt.CallId, Result = evt.ResultJson,
            }];
        }

        return [];
    }

    private static long? ToUnixMs(Google.Protobuf.WellKnownTypes.Timestamp? ts)
    {
        if (ts == null) return null;
        var dt = ts.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
    }
}