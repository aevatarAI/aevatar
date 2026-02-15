// ─────────────────────────────────────────────────────────────
// EventEnvelopeToAGUIEventMapper — EventEnvelope → AGUIEvent 映射
//
// 从 Actor Stream 上的 EventEnvelope 解包 payload，
// 映射为前端可消费的 AG-UI 事件。
// 纯函数，无副作用，每个 envelope 产生 0~N 个 AGUIEvent。
// ─────────────────────────────────────────────────────────────

using Aevatar.Presentation.AGUI;
using AIEvents = Aevatar.AI.Abstractions;
using AGUI = Aevatar.Presentation.AGUI;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

/// <summary>
/// EventEnvelope payload → AG-UI 事件映射。
/// </summary>
public static class EventEnvelopeToAGUIEventMapper
{
    /// <summary>
    /// 将一个 EventEnvelope 映射为 0~N 个 AG-UI 事件。
    /// </summary>
    public static IReadOnlyList<AGUIEvent> Map(EventEnvelope envelope)
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
                ThreadId = ResolveThreadId(envelope, evt.WorkflowName),
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
        if (payload.Is(AIEvents.TextMessageStartEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.TextMessageStartEvent>();
            var msgId = ResolveMessageId(evt.SessionId, envelope.Id);
            return [new AGUI.TextMessageStartEvent
            {
                Timestamp = ts, MessageId = msgId, Role = "assistant",
            }];
        }

        // ─── AI TextMessageContentEvent → AGUI TEXT_MESSAGE_CONTENT ───
        if (payload.Is(AIEvents.TextMessageContentEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.TextMessageContentEvent>();
            var msgId = ResolveMessageId(evt.SessionId, envelope.Id);
            return [new AGUI.TextMessageContentEvent
            {
                Timestamp = ts, MessageId = msgId, Delta = evt.Delta,
            }];
        }

        // ─── AI TextMessageEndEvent → AGUI TEXT_MESSAGE_END ───
        if (payload.Is(AIEvents.TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.TextMessageEndEvent>();
            var msgId = ResolveMessageId(evt.SessionId, envelope.Id);
            return [new AGUI.TextMessageEndEvent
            {
                Timestamp = ts, MessageId = msgId,
            }];
        }

        // ─── ChatResponseEvent → AGUI 完整消息（非流式兼容） ───
        if (payload.Is(AIEvents.ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.ChatResponseEvent>();
            var msgId = ResolveMessageId(evt.SessionId, envelope.Id);
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
                    ThreadId = ResolveThreadId(envelope, evt.WorkflowName),
                    RunId = evt.RunId,
                    Result = new { output = evt.Output },
                }];

            return [new RunErrorEvent
            {
                Timestamp = ts,
                Message = evt.Error,
                RunId = evt.RunId,
                Code = "WORKFLOW_FAILED",
            }];
        }

        // ─── ToolCallEvent → TOOL_CALL_START ───
        if (payload.Is(AIEvents.ToolCallEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.ToolCallEvent>();
            return [new ToolCallStartEvent
            {
                Timestamp = ts, ToolCallId = evt.CallId, ToolName = evt.ToolName,
            }];
        }

        // ─── ToolResultEvent → TOOL_CALL_END ───
        if (payload.Is(AIEvents.ToolResultEvent.Descriptor))
        {
            var evt = payload.Unpack<AIEvents.ToolResultEvent>();
            return [new ToolCallEndEvent
            {
                Timestamp = ts, ToolCallId = evt.CallId, Result = evt.ResultJson,
            }];
        }

        return [];
    }

    private static long? ToUnixMs(Timestamp? ts)
    {
        if (ts == null) return null;
        var dt = ts.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
    }

    private static string ResolveThreadId(EventEnvelope envelope, string fallback)
    {
        return string.IsNullOrWhiteSpace(envelope.PublisherId)
            ? fallback
            : envelope.PublisherId;
    }

    private static string ResolveMessageId(string? sessionId, string? envelopeId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            return $"msg:{sessionId}";

        return $"msg:{envelopeId}";
    }
}
