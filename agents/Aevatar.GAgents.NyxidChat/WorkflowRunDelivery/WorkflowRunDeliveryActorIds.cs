using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;

internal static class WorkflowRunDeliveryActorIds
{
    public static string FromDeliveryId(string deliveryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deliveryId.Trim()));
        return $"workflow-run-delivery:{Convert.ToHexStringLower(hash)}";
    }
}
