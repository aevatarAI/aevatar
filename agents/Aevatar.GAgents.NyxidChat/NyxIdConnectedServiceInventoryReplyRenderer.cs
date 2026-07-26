using System.Text;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdConnectedServiceInventoryReplyRenderer
{
    public static string Render(NyxIdConnectedServiceInventoryQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Failure != NyxIdConnectedServiceInventoryQueryFailure.None ||
            result.Inventory is null)
        {
            return RenderError(result.Failure);
        }

        var inventory = result.Inventory;
        if (inventory.Instances.Count == 0)
            return "你的 NyxID 账号已绑定；当前没有已连接且可用的服务。";

        var reply = new StringBuilder()
            .Append("你的 NyxID 账号已绑定。当前已连接且可用的服务（")
            .Append(inventory.Instances.Count)
            .AppendLine("）：");
        foreach (var instance in inventory.Instances)
        {
            var label = FirstFilled(instance.Label, instance.DisplaySlug, instance.UserServiceId);
            reply.Append("- ").Append(label);
            if (!string.IsNullOrWhiteSpace(instance.DisplaySlug) &&
                !string.Equals(label, instance.DisplaySlug, StringComparison.OrdinalIgnoreCase))
            {
                reply.Append("（").Append(instance.DisplaySlug).Append('）');
            }
            reply.AppendLine();
            reply.Append("  Service ID: ").AppendLine(instance.UserServiceId);
        }
        return reply.ToString().TrimEnd();
    }

    private static string RenderError(NyxIdConnectedServiceInventoryQueryFailure failure) => failure switch
    {
        NyxIdConnectedServiceInventoryQueryFailure.BindingRevoked =>
            "你的 NyxID 绑定记录仍然存在，但 NyxID 已拒绝该绑定凭据；本次无法读取服务清单。",
        NyxIdConnectedServiceInventoryQueryFailure.ScopeUnavailable =>
            "你的 NyxID 账号已绑定，但当前授权 scope 不允许读取服务清单。",
        _ =>
            "你的 NyxID 账号已绑定，但本次未能读取服务清单。请稍后重试。",
    };

    private static string FirstFilled(params string?[] candidates) =>
        candidates.First(static candidate => !string.IsNullOrWhiteSpace(candidate))!.Trim();
}
