using Aevatar.AI.Abstractions;
using Aevatar.AGUI.Contracts;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatAguiSseEventWriter
{
    public static async ValueTask<string?> WriteAsync(
        AGUIEvent aguiEvent,
        string messageId,
        NyxIdChatSseWriter writer,
        CancellationToken ct = default)
    {
        // Refactor (issue1533): Old pattern: a runner-named type implied session/runtime ownership for a presentation mapper.
        // New principle: the endpoint keeps command interaction ownership while this adapter only writes typed AGUI events as NyxID SSE frames.
        switch (aguiEvent.EventCase)
        {
            case AGUIEvent.EventOneofCase.TextMessageStart:
                await writer.WriteTextStartAsync(
                    string.IsNullOrWhiteSpace(aguiEvent.TextMessageStart.MessageId)
                        ? messageId
                        : aguiEvent.TextMessageStart.MessageId,
                    aguiEvent.Sequence,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.TextMessageContent:
                if (!string.IsNullOrEmpty(aguiEvent.TextMessageContent.Delta))
                    await writer.WriteTextDeltaAsync(
                        aguiEvent.TextMessageContent.Delta,
                        aguiEvent.Sequence,
                        ct);
                return null;
            case AGUIEvent.EventOneofCase.TextMessageEnd:
                await writer.WriteTextEndAsync(
                    string.IsNullOrWhiteSpace(aguiEvent.TextMessageEnd.MessageId)
                        ? messageId
                        : aguiEvent.TextMessageEnd.MessageId,
                    aguiEvent.Sequence,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.ModelCallStart:
                await writer.WriteModelCallStartAsync(
                    aguiEvent.ModelCallStart,
                    aguiEvent.Sequence,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.ModelCallEnd:
                await writer.WriteModelCallEndAsync(
                    aguiEvent.ModelCallEnd,
                    aguiEvent.Sequence,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.ToolCallStart:
                await writer.WriteToolCallStartAsync(
                    aguiEvent.ToolCallStart.ToolName,
                    aguiEvent.ToolCallStart.ToolCallId,
                    aguiEvent.ToolCallStart.Presentation,
                    aguiEvent.Sequence,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.ToolCallEnd:
                await writer.WriteToolCallEndAsync(
                    aguiEvent.ToolCallEnd.ToolCallId,
                    aguiEvent.ToolCallEnd.Result ?? string.Empty,
                    aguiEvent.Sequence,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.Custom:
                await WriteCustomAguiEventAsync(
                    aguiEvent.Custom,
                    aguiEvent.Sequence,
                    writer,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.RunError:
                await writer.WriteRunErrorAsync(
                    string.IsNullOrWhiteSpace(aguiEvent.RunError.RunId)
                        ? messageId
                        : aguiEvent.RunError.RunId,
                    aguiEvent.RunError.Code ?? string.Empty,
                    ClassifyError(aguiEvent.RunError.Message ?? string.Empty),
                    aguiEvent.Sequence,
                    ct);
                return "RUN_ERROR";
            case AGUIEvent.EventOneofCase.Usage:
                await writer.WriteUsageAsync(
                    aguiEvent.Usage.Available,
                    aguiEvent.Usage.PromptTokens,
                    aguiEvent.Usage.CompletionTokens,
                    aguiEvent.Usage.TotalTokens,
                    aguiEvent.Usage.Model,
                    aguiEvent.Sequence,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.RunFinished:
                await writer.WriteRunFinishedAsync(
                    string.IsNullOrWhiteSpace(aguiEvent.RunFinished.RunId)
                        ? messageId
                        : aguiEvent.RunFinished.RunId,
                    aguiEvent.RunFinished.Status,
                    aguiEvent.Sequence,
                    ct);
                return "RUN_FINISHED";
            default:
                return null;
        }
    }

    private static async ValueTask WriteCustomAguiEventAsync(
        CustomEvent customEvent,
        long sequence,
        NyxIdChatSseWriter writer,
        CancellationToken ct)
    {
        if (string.Equals(customEvent.Name, "MEDIA_CONTENT", StringComparison.Ordinal) &&
            customEvent.Payload?.Is(MediaContentEvent.Descriptor) == true)
        {
            await writer.WriteMediaContentAsync(
                customEvent.Payload.Unpack<MediaContentEvent>(),
                sequence,
                ct);
            return;
        }

        if (string.Equals(customEvent.Name, "aevatar.llm.reasoning", StringComparison.Ordinal) &&
            customEvent.Payload?.Is(RoleChatReasoningDeltaProgress.Descriptor) == true)
        {
            await writer.WriteReasoningAsync(
                customEvent.Payload.Unpack<RoleChatReasoningDeltaProgress>().Delta,
                sequence,
                ct);
            return;
        }

        if (string.Equals(customEvent.Name, "nyxid.authorization.required", StringComparison.Ordinal) &&
            customEvent.Payload?.Is(NyxIdAuthorizationRequiredEvent.Descriptor) == true)
        {
            await writer.WriteAuthorizationRequiredAsync(
                customEvent.Payload.Unpack<NyxIdAuthorizationRequiredEvent>(),
                sequence,
                ct);
            return;
        }

        if (string.Equals(customEvent.Name, "TOOL_APPROVAL_REQUEST", StringComparison.Ordinal) &&
            customEvent.Payload?.Is(Struct.Descriptor) == true)
        {
            var fields = customEvent.Payload.Unpack<Struct>().Fields;
            await writer.WriteToolApprovalRequestAsync(
                GetString(fields, "requestId"),
                GetString(fields, "toolName"),
                GetString(fields, "toolCallId"),
                GetString(fields, "argumentsJson"),
                GetBool(fields, "isDestructive"),
                GetInt32(fields, "timeoutSeconds"),
                sequence,
                ct);
            return;
        }

        if (TryResolveTypedNyxIdCustomPayload(customEvent, out var typedPayload))
            await writer.WriteTypedCustomEventAsync(customEvent.Name, typedPayload, sequence, ct);
    }

    private static bool TryResolveTypedNyxIdCustomPayload(
        CustomEvent customEvent,
        out Google.Protobuf.IMessage payload)
    {
        payload = null!;
        if (customEvent.Payload is null)
            return false;

        if (string.Equals(
                customEvent.Name,
                NyxIdChatConversationAguiFrameBuilder.TaskSnapshotEventName,
                StringComparison.Ordinal) &&
            customEvent.Payload.Is(NyxIdChatTaskState.Descriptor))
        {
            payload = NyxIdChatTaskPlanWireMapper.FromState(
                customEvent.Payload.Unpack<NyxIdChatTaskState>());
            return true;
        }

        if (string.Equals(
                customEvent.Name,
                NyxIdChatConversationAguiFrameBuilder.TaskStepChangedEventName,
                StringComparison.Ordinal) &&
            customEvent.Payload.Is(NyxIdChatTaskStepChanged.Descriptor))
        {
            payload = NyxIdChatTaskPlanWireMapper.FromState(
                customEvent.Payload.Unpack<NyxIdChatTaskStepChanged>());
            return true;
        }

        if (string.Equals(
                customEvent.Name,
                NyxIdChatConversationAguiFrameBuilder.ControlChangedEventName,
                StringComparison.Ordinal) &&
            customEvent.Payload.Is(NyxIdChatControlFenceState.Descriptor))
        {
            payload = customEvent.Payload.Unpack<NyxIdChatControlFenceState>();
            return true;
        }

        if (string.Equals(
                customEvent.Name,
                NyxIdChatConversationAguiFrameBuilder.ActionRequestEventName,
                StringComparison.Ordinal) &&
            customEvent.Payload.Is(NyxIdAssistantActionRequestWirePayload.Descriptor))
        {
            payload = customEvent.Payload.Unpack<NyxIdAssistantActionRequestWirePayload>();
            return true;
        }

        if (string.Equals(
                customEvent.Name,
                NyxIdChatConversationAguiFrameBuilder.ContinuationChangedEventName,
                StringComparison.Ordinal) &&
            customEvent.Payload.Is(NyxIdChatContinuationAdmissionState.Descriptor))
        {
            payload = customEvent.Payload.Unpack<NyxIdChatContinuationAdmissionState>();
            return true;
        }

        if (string.Equals(
                customEvent.Name,
                NyxIdChatConversationAguiFrameBuilder.StepControlChangedEventName,
                StringComparison.Ordinal) &&
            customEvent.Payload.Is(NyxIdChatStepControlResultState.Descriptor))
        {
            payload = customEvent.Payload.Unpack<NyxIdChatStepControlResultState>();
            return true;
        }

        if (string.Equals(
                customEvent.Name,
                NyxIdChatConversationAguiFrameBuilder.InputRequestEventName,
                StringComparison.Ordinal) &&
            customEvent.Payload.Is(NyxIdChatPendingInputState.Descriptor))
        {
            payload = customEvent.Payload.Unpack<NyxIdChatPendingInputState>();
            return true;
        }

        if (string.Equals(
                customEvent.Name,
                NyxIdChatConversationAguiFrameBuilder.InputChangedEventName,
                StringComparison.Ordinal) &&
            customEvent.Payload.Is(NyxIdChatInputResolutionState.Descriptor))
        {
            payload = customEvent.Payload.Unpack<NyxIdChatInputResolutionState>();
            return true;
        }

        if (string.Equals(
                customEvent.Name,
                NyxIdChatConversationAguiFrameBuilder.ApprovalRequestEventName,
                StringComparison.Ordinal) &&
            customEvent.Payload.Is(NyxIdChatPendingApprovalState.Descriptor))
        {
            payload = customEvent.Payload.Unpack<NyxIdChatPendingApprovalState>();
            return true;
        }

        if (string.Equals(
                customEvent.Name,
                NyxIdChatConversationAguiFrameBuilder.ApprovalChangedEventName,
                StringComparison.Ordinal) &&
            customEvent.Payload.Is(NyxIdChatApprovalResolutionState.Descriptor))
        {
            payload = customEvent.Payload.Unpack<NyxIdChatApprovalResolutionState>();
            return true;
        }

        return false;
    }

    private static string GetString(IDictionary<string, Value> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value.StringValue ?? string.Empty : string.Empty;

    private static bool GetBool(IDictionary<string, Value> fields, string key) =>
        fields.TryGetValue(key, out var value) && value.BoolValue;

    private static int GetInt32(IDictionary<string, Value> fields, string key) =>
        fields.TryGetValue(key, out var value) ? (int)value.NumberValue : 0;

    private static string ClassifyError(string error) => NyxIdRelayReplies.ClassifyError(error);
}
