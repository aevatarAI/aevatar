using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgents.ChatHistory;

public static class ChatHistoryActorIds
{
    public static string Conversation(string scopeId, string conversationId) =>
        $"chat-conversation:{HashTuple(scopeId, conversationId)}";

    public static string LegacyConversation(string scopeId, string conversationId) =>
        $"chat-{scopeId.Trim()}-{conversationId.Trim()}";

    public static string TurnDelivery(string workflowActorId, string workflowCommandId) =>
        $"chat-history-delivery-{workflowActorId.Trim()}-{workflowCommandId.Trim()}";

    public static string CreateConversationId(string scopeId, string createIdempotencyKey) =>
        $"conversation-{HashTuple(scopeId, createIdempotencyKey)}";

    public static string CreateTurnId(string scopeId, string createIdempotencyKey) =>
        $"turn-{HashTuple(scopeId, createIdempotencyKey)}";

    public static string CreateDeliveryId(string scopeId, string createIdempotencyKey) =>
        $"chat-history-delivery-create-{HashTuple(scopeId, createIdempotencyKey)}";

    private static string HashTuple(params string[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var normalized = part.Trim();
            var byteCount = Encoding.UTF8.GetByteCount(normalized);
            builder.Append(byteCount).Append(':').Append(normalized);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
