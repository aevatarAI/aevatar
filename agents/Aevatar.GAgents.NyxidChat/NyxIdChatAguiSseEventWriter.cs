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
        NyxIdChatSseWriter writer)
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
                    CancellationToken.None);
                return null;
            case AGUIEvent.EventOneofCase.TextMessageContent:
                if (!string.IsNullOrEmpty(aguiEvent.TextMessageContent.Delta))
                    await writer.WriteTextDeltaAsync(aguiEvent.TextMessageContent.Delta, CancellationToken.None);
                return null;
            case AGUIEvent.EventOneofCase.TextMessageEnd:
                await writer.WriteTextEndAsync(
                    string.IsNullOrWhiteSpace(aguiEvent.TextMessageEnd.MessageId)
                        ? messageId
                        : aguiEvent.TextMessageEnd.MessageId,
                    CancellationToken.None);
                return null;
            case AGUIEvent.EventOneofCase.ToolCallStart:
                await writer.WriteToolCallStartAsync(
                    aguiEvent.ToolCallStart.ToolName,
                    aguiEvent.ToolCallStart.ToolCallId,
                    CancellationToken.None);
                return null;
            case AGUIEvent.EventOneofCase.ToolCallEnd:
                await writer.WriteToolCallEndAsync(
                    aguiEvent.ToolCallEnd.ToolCallId,
                    aguiEvent.ToolCallEnd.Result ?? string.Empty,
                    CancellationToken.None);
                return null;
            case AGUIEvent.EventOneofCase.Custom:
                await WriteCustomAguiEventAsync(aguiEvent.Custom, writer);
                return null;
            case AGUIEvent.EventOneofCase.RunError:
                await writer.WriteRunErrorAsync(
                    ClassifyError(aguiEvent.RunError.Message ?? string.Empty),
                    CancellationToken.None);
                return "RUN_ERROR";
            case AGUIEvent.EventOneofCase.RunFinished:
                await writer.WriteRunFinishedAsync(CancellationToken.None);
                return "RUN_FINISHED";
            default:
                return null;
        }
    }

    private static async ValueTask WriteCustomAguiEventAsync(CustomEvent customEvent, NyxIdChatSseWriter writer)
    {
        if (string.Equals(customEvent.Name, "MEDIA_CONTENT", StringComparison.Ordinal) &&
            customEvent.Payload?.Is(MediaContentEvent.Descriptor) == true)
        {
            await writer.WriteMediaContentAsync(customEvent.Payload.Unpack<MediaContentEvent>(), CancellationToken.None);
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
                CancellationToken.None);
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
