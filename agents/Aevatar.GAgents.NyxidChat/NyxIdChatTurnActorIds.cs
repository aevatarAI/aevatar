using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgents.NyxidChat;

public static class NyxIdChatTurnActorIds
{
    public static string ForTurn(string conversationActorId, string turnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        var conversation = conversationActorId.Trim();
        var turn = turnId.Trim();
        var identity = $"{conversation.Length}:{conversation}{turn.Length}:{turn}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"nyxid-chat-turn:{Convert.ToHexStringLower(hash)[..32]}";
    }
}
