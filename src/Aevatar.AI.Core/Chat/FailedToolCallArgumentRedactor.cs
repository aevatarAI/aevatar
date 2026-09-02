using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Tools;

namespace Aevatar.AI.Core.Chat;

internal static class FailedToolCallArgumentRedactor
{
    private const string RedactedArgumentsJson = "{}";

    public static ChatMessage Redact(
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        ChatMessage assistantMessage,
        ToolExecutionResult result)
    {
        if (!ShouldRedact(result) ||
            assistantMessage.ToolCalls is not { Count: > 0 } toolCalls ||
            !toolCalls.Any(call => string.Equals(call.Id, result.CallId, StringComparison.Ordinal)))
        {
            return assistantMessage;
        }

        var redactedMessage = new ChatMessage
        {
            Role = assistantMessage.Role,
            Content = assistantMessage.Content,
            ReasoningContent = assistantMessage.ReasoningContent,
            ContentParts = assistantMessage.ContentParts,
            ToolCallId = assistantMessage.ToolCallId,
            ToolCalls = toolCalls.Select(call => new ToolCall
            {
                Id = call.Id,
                Name = call.Name,
                ArgumentsJson = string.Equals(call.Id, result.CallId, StringComparison.Ordinal)
                    ? RedactedArgumentsJson
                    : call.ArgumentsJson,
            }).ToArray(),
            ToolResultView = assistantMessage.ToolResultView,
        };

        Replace(messages, assistantMessage, redactedMessage);
        Replace(pendingHistoryMessages, assistantMessage, redactedMessage);
        return redactedMessage;
    }

    private static bool ShouldRedact(ToolExecutionResult result) =>
        result.IsError ||
        result.Receipt?.Status is AgentToolReceiptStatus.Error or
            AgentToolReceiptStatus.Denied or
            AgentToolReceiptStatus.AuthorizationRequired;

    private static void Replace(
        List<ChatMessage> messages,
        ChatMessage existingMessage,
        ChatMessage replacementMessage)
    {
        var index = messages.FindIndex(message => ReferenceEquals(message, existingMessage));
        if (index >= 0)
            messages[index] = replacementMessage;
    }
}
