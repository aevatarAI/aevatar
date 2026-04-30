using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;

namespace Aevatar.GAgents.Channel.Identity.Slash;

/// <summary>
/// /whoami — show the inbound sender their current binding state. Always
/// requires a binding; the runner short-circuits unbound senders to the
/// /init prompt before invoking the handler.
/// </summary>
public sealed class WhoamiChannelSlashCommandHandler : IChannelSlashCommandHandler
{
    public string Name => "whoami";

    public bool RequiresBinding => true;

    public Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bindingId = context.BindingIdValue ?? string.Empty;
        var senderName = string.IsNullOrWhiteSpace(context.SenderName)
            ? context.SenderId
            : context.SenderName;

        var lines = new[]
        {
            $"已绑定 NyxID 账号。",
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
