using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class LarkRemoteToolApprovalNotificationPort : IRemoteToolApprovalNotificationPort
{
    private readonly FeishuCardOutboundMessageSender _sender;
    private readonly LarkMessageComposer _composer;
    private readonly ILogger<LarkRemoteToolApprovalNotificationPort> _logger;

    public LarkRemoteToolApprovalNotificationPort(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        NyxIdApiClient nyxIdApiClient,
        LarkMessageComposer composer,
        ILogger<LarkRemoteToolApprovalNotificationPort> logger,
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

        var deliveryTargetId = Normalize(notification.ToolContext.Channel.DeliveryTargetId);
        if (deliveryTargetId is null)
            throw new InvalidOperationException("Remote approval notification requires an explicit delivery target id.");

        var target = await _sender.ResolveTargetAsync(
            deliveryTargetId,
            "remote tool approval notification",
            ct);

        var payload = _composer.Compose(BuildIntent(notification), BuildComposeContext());
        if (!payload.IsInteractive)
            throw new InvalidOperationException("Remote approval notification must render as an interactive Lark card.");

        await _sender.SendInteractiveCardMessageAsync(
            target,
            payload.ContentJson,
            "Lark remote approval card delivery returned empty response.",
            "Lark remote approval card delivery failed",
            ct);

        _logger.LogInformation(
            "Delivered remote tool approval card: target={DeliveryTargetId}, request={RequestId}, remote={RemoteApprovalId}",
            deliveryTargetId,
            notification.Request.RequestId,
            notification.Submission.RemoteApprovalId);
    }

    internal static MessageContent BuildIntent(RemoteToolApprovalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var card = new CardBlock
        {
            Kind = CardBlockKind.Section,
            Title = "Tool Approval Required",
            Text = BuildMarkdown(notification),
        };
        card.Actions.Add(BuildDecisionAction("nyxid-approval-approve", "Approve", true, true, false, notification));
        card.Actions.Add(BuildDecisionAction("nyxid-approval-reject", "Reject", false, false, true, notification));

        var content = new MessageContent { Text = string.Empty };
        content.Cards.Add(card);
        return content;
    }

    private static ActionElement BuildDecisionAction(
        string actionId,
        string label,
        bool approved,
        bool primary,
        bool danger,
        RemoteToolApprovalNotification notification) =>
        new()
        {
            Kind = ActionElementKind.Button,
            ActionId = actionId,
            Label = label,
            IsPrimary = primary,
            IsDanger = danger,
            NyxIdApproval = new NyxIdApprovalActionPayload
            {
                RequestId = notification.Submission.RemoteApprovalId,
                Approved = approved,
            },
        };

    private static string BuildMarkdown(RemoteToolApprovalNotification notification)
    {
        var request = notification.Request;
        var lines = new List<string>
        {
            $"Tool: `{request.ToolName}`",
            $"Request: `{notification.Submission.RemoteApprovalId}`",
            $"Local request: `{request.RequestId}`",
            $"Destructive: `{request.IsDestructive.ToString().ToLowerInvariant()}`",
        };

        if (notification.Submission.ExpiresAt is { } expiresAt)
            lines.Add($"Expires: `{expiresAt:O}`");

        if (!string.IsNullOrWhiteSpace(request.ArgumentsJson))
        {
            lines.Add(string.Empty);
            lines.Add("Arguments:");
            lines.Add($"```json\n{request.ArgumentsJson}\n```");
        }

        return string.Join('\n', lines);
    }

    private static ComposeContext BuildComposeContext() => new()
    {
        Capabilities = LarkMessageComposer.DefaultCapabilities.Clone(),
    };

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
