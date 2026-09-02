using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity.Slash;

/// <summary>
/// /init — start a new NyxID OAuth Authorization Code + PKCE binding flow for
/// the inbound sender. Renders the authorize URL as a Lark interactive card
/// (button) when the channel supports cards, with a plain-text fallback for
/// transports that don't. ADR-0018 §Decision: only emits the URL in private
/// chats; group/channel inbound is refused so the sealed state token never
/// reaches a third party.
/// </summary>
public sealed class InitChannelSlashCommandHandler : IChannelSlashCommandHandler
{
    private readonly INyxIdCapabilityBroker _broker;
    private readonly ILogger<InitChannelSlashCommandHandler> _logger;

    public InitChannelSlashCommandHandler(
        INyxIdCapabilityBroker broker,
        ILogger<InitChannelSlashCommandHandler> logger)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "init";

    public bool RequiresBinding => false;

    public ChannelSlashCommandUsage Usage => new(
        Name,
        string.Empty,
        "发起 NyxID 账号绑定");

    public async Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsPrivateChat)
        {
            // Refuse to emit the authorize URL outside private chats. The
            // state token includes the sealed PKCE verifier; leaking it to a
            // group lets any participant complete the OAuth dance and bind a
            // different external subject. See ADR-0018 §Decision (private-DM-
            // only authorize URL emission).
            return PlainText("为安全起见,/init 只能在与 bot 的私聊中发起。请先与 bot 私聊后再发送 /init。");
        }

        BindingChallenge challenge;
        try
        {
            challenge = await _broker.StartExternalBindingAsync(context.Subject, ct).ConfigureAwait(false);
        }
        catch (AevatarOAuthClientNotProvisionedException ex)
        {
            // Cluster cold-start: configured client materialization is still
            // running. Distinct from a real broker failure —
            // the user retrying in 30s typically resolves it without ops
            // intervention. Logged at Information so the gap shows up in
            // dashboards but does not page on every silo restart.
            _logger.LogInformation(ex,
                "/init received before aevatar OAuth client bootstrap finished; subject={Platform}:{Tenant}:{Sender}",
                context.Subject.Platform, context.Subject.Tenant, context.Subject.ExternalUserId);
            return PlainText("aevatar 正在初始化 NyxID 客户端,请 30 秒后重新发送 /init。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "/init failed to start external binding for subject={Platform}:{Tenant}:{Sender}",
                context.Subject.Platform, context.Subject.Tenant, context.Subject.ExternalUserId);
            return PlainText("启动 NyxID 绑定时遇到内部错误,请稍后重试 /init。");
        }

        return BuildBindingCard(challenge.AuthorizeUrl, challenge.RenewsExistingBinding);
    }

    private static MessageContent PlainText(string text) => new() { Text = text };

    /// <summary>
    /// Build a Lark-friendly card (header + description + primary "open url"
    /// button). Channels without card support degrade to plain text via
    /// <see cref="MessageContent.Text"/> being set as the fallback.
    /// </summary>
    public static MessageContent BuildBindingCard(
        string authorizeUrl,
        bool renewsExistingBinding = false)
    {
        var content = new MessageContent
        {
            Text = renewsExistingBinding
                ? $"打开此链接重新确认并更新 Lark bot 的 NyxID 服务授权(5 分钟内有效):\n{authorizeUrl}"
                : $"打开此链接完成 NyxID 登录并确认服务授权(5 分钟内有效):\n{authorizeUrl}",
        };
        content.Cards.Add(new CardBlock
        {
            Title = renewsExistingBinding ? "更新 NyxID 服务授权" : "完成 NyxID 绑定",
            Text = renewsExistingBinding
                ? "重新确认服务授权；成功后会安全更新当前 Lark 绑定。链接 5 分钟内有效。"
                : "登录并确认 Lark bot 可使用的 NyxID 服务。链接 5 分钟内有效。",
        });
        content.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Link,
            ActionId = "nyxid_init_open",
            Label = renewsExistingBinding ? "更新服务授权" : "立即绑定 NyxID",
            Value = authorizeUrl,
            IsPrimary = true,
        });
        return content;
    }
}
