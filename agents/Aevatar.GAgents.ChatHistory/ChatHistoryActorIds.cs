using System.Security.Cryptography;
using System.Text;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgents.ChatHistory;

public static class ChatHistoryActorIds
{
    public static string Conversation(string scopeId, string conversationId) =>
        $"chat-conversation:{HashTuple(Normalize(scopeId), Normalize(conversationId))}";

    public static string LegacyConversation(string scopeId, string conversationId) =>
        $"chat-{Normalize(scopeId)}-{Normalize(conversationId)}";

    public static string TurnDelivery(string workflowActorId, string workflowCommandId) =>
        $"chat-history-delivery-{workflowActorId.Trim()}-{workflowCommandId.Trim()}";

    public static string CreateConversationId(string scopeId, string workflowCommandId) =>
        $"chatc-{HashTuple(Normalize(scopeId), Normalize(workflowCommandId))[..32]}";

    public static string CreateTurnId(string scopeId, string workflowCommandId) =>
        $"turn-{HashTuple(Normalize(scopeId), Normalize(workflowCommandId), "turn")[..32]}";

    private static string HashTuple(params string[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetByteCount(part);
            builder.Append(bytes);
            builder.Append(':');
            builder.Append(part);
            builder.Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

public static class ChatHistoryCreateRecoveryIds
{
    public static string FromScopeAndCommandId(string scopeId, string commandId) =>
        WorkflowChatHistoryCreateRecoveryIds.FromScopeAndCommandId(scopeId, commandId);
}
