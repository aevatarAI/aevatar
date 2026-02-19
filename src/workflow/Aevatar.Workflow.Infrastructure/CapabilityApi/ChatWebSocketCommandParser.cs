using System.Text.Json;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal readonly record struct ChatWebSocketCommandEnvelope(
    string RequestId,
    ChatInput Input);

internal readonly record struct ChatWebSocketCommandParseError(
    string Code,
    string Message,
    string? RequestId = null);

internal static class ChatWebSocketCommandParser
{
    public static bool TryParse(
        string? rawCommand,
        out ChatWebSocketCommandEnvelope commandEnvelope,
        out ChatWebSocketCommandParseError parseError)
    {
        commandEnvelope = default;
        parseError = default;

        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            parseError = new ChatWebSocketCommandParseError(
                "EMPTY_COMMAND",
                "Command payload is required.");
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

        if (command?.Payload == null || !string.Equals(command.Type, "chat.command", StringComparison.Ordinal))
        {
            parseError = new ChatWebSocketCommandParseError(
                "INVALID_COMMAND",
                "Expected { type: 'chat.command', payload: { prompt, workflow?, agentId? } }.");
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
                requestId);
            return false;
        }

        commandEnvelope = new ChatWebSocketCommandEnvelope(requestId, command.Payload);
        return true;
    }
}
