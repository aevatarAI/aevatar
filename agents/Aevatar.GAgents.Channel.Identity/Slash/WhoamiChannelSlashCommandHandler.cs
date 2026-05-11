using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;

namespace Aevatar.GAgents.Channel.Identity.Slash;

/// <summary>
/// /whoami — show the inbound sender their current binding state. Issue #513
/// Phase 6 specifies <c>/init</c>, <c>/unbind</c>, and <c>/whoami</c> do NOT
/// require a binding so an unbound sender can introspect their own state
/// without being bounced through the binding gate. Bound senders see masked
/// binding info; unbound senders see "未绑定" with a /init hint.
/// </summary>
public sealed class WhoamiChannelSlashCommandHandler : IChannelSlashCommandHandler
{
    public string Name => "whoami";

    public bool RequiresBinding => false;

    public ChannelSlashCommandUsage Usage => new(
        Name,
        string.Empty,
        "查看当前聊天用户的 NyxID 绑定状态");

    public Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bindingId = context.BindingIdValue ?? string.Empty;
        var senderName = string.IsNullOrWhiteSpace(context.SenderName)
            ? context.SenderId
            : context.SenderName;

        var lines = string.IsNullOrEmpty(bindingId)
            ? new[]
            {
                "未绑定 NyxID 账号。",
                $"- 平台账号:{senderName}",
                $"- 平台:{context.Subject.Platform}",
                "发送 /init 完成绑定。",
            }
            : new[]
            {
                "已绑定 NyxID 账号。",
                $"- 平台账号:{senderName}",
                $"- Binding ID:{Mask(bindingId)}",
                $"- 平台:{context.Subject.Platform}",
            };

        var reply = new MessageContent
        {
            Text = string.Join('\n', lines),
        };
        return Task.FromResult<MessageContent?>(reply);
    }

    /// <summary>
    /// Show only a short prefix + suffix of the binding-id so a screenshot of
    /// /whoami doesn't leak a token-sized identifier into a public chat. The
    /// binding-id is opaque (no inherent secret value), but redacting is a
    /// cheap belt-and-braces.
    /// </summary>
    private static string Mask(string bindingId)
    {
        if (string.IsNullOrEmpty(bindingId)) return "(unknown)";
        return bindingId.Length <= 10
            ? bindingId
            : bindingId[..4] + "…" + bindingId[^4..];
    }
}
