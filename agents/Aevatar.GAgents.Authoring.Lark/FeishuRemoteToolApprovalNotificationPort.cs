using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class FeishuRemoteToolApprovalNotificationPort : IRemoteToolApprovalNotificationPort
{
    private readonly FeishuCardOutboundMessageSender _sender;
    private readonly LarkMessageComposer _composer;
    private readonly ILogger<FeishuRemoteToolApprovalNotificationPort> _logger;

    public FeishuRemoteToolApprovalNotificationPort(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        NyxIdApiClient nyxIdApiClient,
        LarkMessageComposer composer,
        ILogger<FeishuRemoteToolApprovalNotificationPort> logger,
        ILarkOutboundDispatcher? larkOutboundDispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(deliveryTargetReader);
        ArgumentNullException.ThrowIfNull(nyxIdApiClient);
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sender = new FeishuCardOutboundMessageSender(
            deliveryTargetReader,
            nyxIdApiClient,
            logger,
            larkOutboundDispatcher);
    }

    public async Task NotifyAsync(RemoteToolApprovalNotification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var deliveryTargetId = ResolveDeliveryTargetId(notification);
        var target = await _sender.ResolveTargetAsync(
            deliveryTargetId,
            "remote tool approval",
            ct);

        await _sender.SendInteractiveCardMessageAsync(
            target,
            BuildCardJson(notification, _composer),
            "Feishu remote tool approval notification delivery returned empty response.",
            "Feishu remote tool approval notification delivery failed",
            ct);

        _logger.LogInformation(
            "Delivered remote tool approval card: target={DeliveryTargetId}, request={RequestId}, remote={RemoteApprovalId}",
            deliveryTargetId,
            notification.RequestId,
            notification.RemoteApprovalId);
    }

    internal static string BuildCardJson(RemoteToolApprovalNotification notification) =>
        BuildCardJson(notification, new LarkMessageComposer());

    internal static string BuildCardJson(
        RemoteToolApprovalNotification notification,
        LarkMessageComposer composer)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(composer);

        var payload = composer.Compose(BuildIntent(notification), BuildComposeContext());
        if (!payload.IsInteractive)
            throw new InvalidOperationException("Remote tool approval notification must render as an interactive Lark card.");

        return payload.ContentJson;
    }

    internal static MessageContent BuildIntent(RemoteToolApprovalNotification notification)
    {
        var intent = new MessageContent
        {
            Text = string.Empty,
            Disposition = MessageDisposition.Normal,
        };

        var card = new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = "Tool approval required.",
            Text = BuildMarkdown(notification),
        };
        intent.Cards.Add(card);
        intent.Actions.Add(BuildButton(notification, approved: true));
        intent.Actions.Add(BuildButton(notification, approved: false));
        return intent;
    }

    private static ActionElement BuildButton(RemoteToolApprovalNotification notification, bool approved) =>
        new()
        {
            Kind = ActionElementKind.Button,
            ActionId = approved ? "nyxid_approval_approve" : "nyxid_approval_deny",
            Label = approved ? "Approve" : "Deny",
            IsPrimary = approved,
            IsDanger = !approved,
            NyxidApproval = new NyxIdApprovalActionPayload
            {
                RequestId = notification.RequestId,
                RemoteApprovalId = notification.RemoteApprovalId,
                Approved = approved,
            },
        };

    private static string BuildMarkdown(RemoteToolApprovalNotification notification)
    {
        var lines = new List<string>
        {
            $"Tool: `{notification.ToolName}`",
            $"Request: `{notification.RequestId}`",
        };

        if (notification.IsDestructive)
            lines.Add("This tool call is marked destructive.");

        if (notification.ExpiresAt is { } expiresAt)
            lines.Add($"Expires: {expiresAt:O}");

        if (!string.IsNullOrWhiteSpace(notification.ArgumentsJson))
        {
            lines.Add(string.Empty);
            lines.Add("Arguments:");
            lines.Add(TruncateArguments(notification.ArgumentsJson));
        }

        return string.Join('\n', lines);
    }

    private static string TruncateArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            argumentsJson = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            // Keep the original boundary payload when it is not JSON; it is still useful context.
        }

        const int maxLength = 1_500;
        return argumentsJson.Length <= maxLength
            ? $"```json\n{argumentsJson}\n```"
            : $"```json\n{argumentsJson[..maxLength]}\n...[truncated]\n```";
    }

    private static string ResolveDeliveryTargetId(RemoteToolApprovalNotification notification)
    {
        var context = notification.ToolContext;
        var deliveryTargetId =
            NormalizeOptional(context.Channel.MessageId) ??
            NormalizeOptional(context.Channel.PlatformMessageId) ??
            NormalizeOptional(context.Caller.ResponseId) ??
            TryGetExternalMetadata(context, ChannelMetadataKeys.MessageId) ??
            TryGetExternalMetadata(context, ChannelMetadataKeys.PlatformMessageId);

        if (deliveryTargetId is null)
            throw new InvalidOperationException("Remote tool approval notification requires an actor/catalog-owned delivery target id.");

        return deliveryTargetId;
    }

    private static string? TryGetExternalMetadata(AgentToolExecutionContext context, string key)
    {
        return context.ExternalMetadata.TryGetValue(key, out var value)
            ? NormalizeOptional(value)
            : null;
    }

    private static ComposeContext BuildComposeContext() => new()
    {
        Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
    };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
