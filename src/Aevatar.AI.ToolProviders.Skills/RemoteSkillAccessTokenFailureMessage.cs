namespace Aevatar.AI.ToolProviders.Skills;

/// <summary>
/// Builds actionable, credential-free guidance for remote-skill token failures.
/// </summary>
public static class RemoteSkillAccessTokenFailureMessage
{
    public static string Build(RemoteSkillAccessTokenFailureKind failureKind) => failureKind switch
    {
        RemoteSkillAccessTokenFailureKind.ChannelBindingRequired =>
            "当前无法获取 NyxID 凭证。请在与 Bot 的私聊中发送 /init 完成 NyxID 账号绑定，然后重试。",
        RemoteSkillAccessTokenFailureKind.ChannelBindingRefreshRequired =>
            "当前 NyxID 绑定无法签发凭证。请在与 Bot 的私聊中发送 /init 更新 NyxID 账号绑定，然后重试。",
        _ => "当前暂时无法获取 NyxID 凭证，请稍后重试。",
    };
}
