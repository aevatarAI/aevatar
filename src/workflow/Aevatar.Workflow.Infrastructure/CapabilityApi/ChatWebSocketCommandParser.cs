using System.Net.WebSockets;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal readonly record struct ChatWebSocketCommandEnvelope(
    string RequestId,
    ChatInput Input,
    WebSocketMessageType ResponseMessageType);

internal readonly record struct ChatWebSocketCommandParseError(
    string Code,
    string Message,
    string? RequestId = null,
    WebSocketMessageType ResponseMessageType = WebSocketMessageType.Text);

internal static class ChatWebSocketCommandParser
{
    public static bool TryParse(
        ChatWebSocketInboundFrame? incomingFrame,
        out ChatWebSocketCommandEnvelope commandEnvelope,
        out ChatWebSocketCommandParseError parseError)
    {
        commandEnvelope = default;
        parseError = default;

        var responseMessageType = incomingFrame.HasValue
            ? ChatWebSocketProtocol.NormalizeMessageType(incomingFrame.Value.MessageType)
            : WebSocketMessageType.Text;

        if (incomingFrame == null)
        {
            parseError = new ChatWebSocketCommandParseError(
                "EMPTY_COMMAND",
                "Command payload is required.",
                ResponseMessageType: responseMessageType);
            return false;
        }

        if (!ChatWebSocketProtocol.TryDecodeUtf8(incomingFrame.Value.Payload, out var rawCommand))
        {
            parseError = new ChatWebSocketCommandParseError(
                "INVALID_COMMAND_ENCODING",
                "Command payload must be UTF-8 encoded JSON.",
                ResponseMessageType: responseMessageType);
            return false;
        }

        return TryParse(
            rawCommand,
            responseMessageType,
            out commandEnvelope,
            out parseError);
    }

    private static bool TryParse(
        string? rawCommand,
        WebSocketMessageType responseMessageType,
        out ChatWebSocketCommandEnvelope commandEnvelope,
        out ChatWebSocketCommandParseError parseError)
    {
        commandEnvelope = default;
        parseError = default;

        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            parseError = new ChatWebSocketCommandParseError(
                "EMPTY_COMMAND",
                "Command payload is required.",
                ResponseMessageType: responseMessageType);
            return false;
        }

        ChatWsCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<ChatWsCommand>(rawCommand, ChatWebSocketProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            command = null;
        }

        if (command?.Payload == null || !string.Equals(command.Type, ChatCapabilityMessageTypes.ChatCommand, StringComparison.Ordinal))
        {
            parseError = new ChatWebSocketCommandParseError(
                "INVALID_COMMAND",
                "Expected { type: 'chat.command', payload: { prompt, workflow?, agentId? } }.",
                ResponseMessageType: responseMessageType);
            return false;
        }

        var requestId = string.IsNullOrWhiteSpace(command.RequestId)
            ? Guid.NewGuid().ToString("N")
            : command.RequestId;

        if (string.IsNullOrWhiteSpace(command.Payload.Prompt))
        {
            parseError = new ChatWebSocketCommandParseError(
                "INVALID_PROMPT",
                "Prompt is required.",
                requestId,
                responseMessageType);
            return false;
        }

        commandEnvelope = new ChatWebSocketCommandEnvelope(requestId, command.Payload, responseMessageType);
        return true;
    }
}
