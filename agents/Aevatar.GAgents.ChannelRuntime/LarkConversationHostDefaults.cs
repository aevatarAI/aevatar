namespace Aevatar.GAgents.ChannelRuntime;

internal static class LarkConversationHostDefaults
{
    public const string HttpClientName = "Aevatar.GAgents.Channel.Lark";

    public static readonly Uri BaseAddress = new("https://open.feishu.cn", UriKind.Absolute);
}
