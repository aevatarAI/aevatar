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
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.TextMessageContent:
                if (!string.IsNullOrEmpty(aguiEvent.TextMessageContent.Delta))
                    await writer.WriteTextDeltaAsync(aguiEvent.TextMessageContent.Delta, ct);
                return null;
            case AGUIEvent.EventOneofCase.TextMessageEnd:
                await writer.WriteTextEndAsync(
                    string.IsNullOrWhiteSpace(aguiEvent.TextMessageEnd.MessageId)
                        ? messageId
                        : aguiEvent.TextMessageEnd.MessageId,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.ToolCallStart:
                await writer.WriteToolCallStartAsync(
                    aguiEvent.ToolCallStart.ToolName,
                    aguiEvent.ToolCallStart.ToolCallId,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.ToolCallEnd:
                await writer.WriteToolCallEndAsync(
                    aguiEvent.ToolCallEnd.ToolCallId,
                    aguiEvent.ToolCallEnd.Result ?? string.Empty,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.Custom:
                await WriteCustomAguiEventAsync(aguiEvent.Custom, writer, ct);
                return null;
            case AGUIEvent.EventOneofCase.RunError:
                await writer.WriteRunErrorAsync(
                    string.IsNullOrWhiteSpace(aguiEvent.RunError.RunId)
                        ? messageId
                        : aguiEvent.RunError.RunId,
                    aguiEvent.RunError.Code ?? string.Empty,
                    ClassifyError(aguiEvent.RunError.Message ?? string.Empty),
                    ct);
                return "RUN_ERROR";
            case AGUIEvent.EventOneofCase.Usage:
                await writer.WriteUsageAsync(
                    aguiEvent.Usage.Available,
                    aguiEvent.Usage.PromptTokens,
                    aguiEvent.Usage.CompletionTokens,
                    aguiEvent.Usage.TotalTokens,
                    aguiEvent.Usage.Model,
                    ct);
                return null;
            case AGUIEvent.EventOneofCase.RunFinished:
                await writer.WriteRunFinishedAsync(
                    string.IsNullOrWhiteSpace(aguiEvent.RunFinished.RunId)
                        ? messageId
                        : aguiEvent.RunFinished.RunId,
                    aguiEvent.RunFinished.Status,
                    ct);
                return "RUN_FINISHED";
            default:
                return null;
        }
    }

    private static async ValueTask WriteCustomAguiEventAsync(
        CustomEvent customEvent,
        NyxIdChatSseWriter writer,
        CancellationToken ct)
    {
        if (string.Equals(customEvent.Name, "MEDIA_CONTENT", StringComparison.Ordinal) &&
            customEvent.Payload?.Is(MediaContentEvent.Descriptor) == true)
        {
            await writer.WriteMediaContentAsync(customEvent.Payload.Unpack<MediaContentEvent>(), ct);
            return;
        }

        if (string.Equals(customEvent.Name, "nyxid.authorization.required", StringComparison.Ordinal) &&
            customEvent.Payload?.Is(NyxIdAuthorizationRequiredEvent.Descriptor) == true)
        {
            await writer.WriteAuthorizationRequiredAsync(
                customEvent.Payload.Unpack<NyxIdAuthorizationRequiredEvent>(),
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
                ct);
        }
    }

    private static string GetString(IDictionary<string, Value> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value.StringValue ?? string.Empty : string.Empty;

    private static bool GetBool(IDictionary<string, Value> fields, string key) =>
        fields.TryGetValue(key, out var value) && value.BoolValue;

    private static int GetInt32(IDictionary<string, Value> fields, string key) =>
        fields.TryGetValue(key, out var value) ? (int)value.NumberValue : 0;

    private static string ClassifyError(string error) => NyxIdRelayReplies.ClassifyError(error);
}
