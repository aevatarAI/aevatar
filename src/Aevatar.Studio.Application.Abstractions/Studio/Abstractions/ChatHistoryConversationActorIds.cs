using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public static class ChatHistoryConversationActorIds
{
    public static string Canonical(string scopeId, string conversationId) =>
        $"chat-conversation:{HashTuple(Normalize(scopeId), Normalize(conversationId))}";

    public static string Legacy(string scopeId, string conversationId) =>
        $"chat-{Normalize(scopeId)}-{Normalize(conversationId)}";

    private static string HashTuple(params string[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            builder.Append(Encoding.UTF8.GetByteCount(part));
            builder.Append(':');
            builder.Append(part);
            builder.Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
