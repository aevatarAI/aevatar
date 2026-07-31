using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgents.ChatHistory;

public static class ChatTurnHistoryDeliveryActorIds
{
    public static string FromDeliveryId(string deliveryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deliveryId.Trim()));
        return $"chat-history-delivery:{Convert.ToHexStringLower(hash)}";
    }
}
