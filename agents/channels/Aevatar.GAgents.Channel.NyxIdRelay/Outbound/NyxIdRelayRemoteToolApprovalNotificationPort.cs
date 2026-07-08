using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

public sealed class NyxIdRelayRemoteToolApprovalNotificationPort : IRemoteToolApprovalNotificationPort
{
    private readonly ChannelDeliveryTargetResolver _targetResolver;
    private readonly LarkMessageComposer _composer;
    private readonly IChannelNativeMessageSender _larkSender;
    private readonly ILogger<NyxIdRelayRemoteToolApprovalNotificationPort> _logger;

    public NyxIdRelayRemoteToolApprovalNotificationPort(
        ChannelDeliveryTargetResolver targetResolver,
        LarkMessageComposer composer,
        LarkChannelNativeMessageSender larkSender,
        ILogger<NyxIdRelayRemoteToolApprovalNotificationPort> logger)
    {
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _larkSender = larkSender ?? throw new ArgumentNullException(nameof(larkSender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task NotifyAsync(RemoteToolApprovalNotification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var deliveryTargetId = Normalize(notification.ToolContext.Channel.DeliveryTargetId);
        if (deliveryTargetId is null)
            throw new InvalidOperationException("Remote approval notification requires an explicit delivery target id.");

        var target = await _targetResolver.ResolveAsync(
                deliveryTargetId,
                "remote tool approval notification",
                ct)
            .ConfigureAwait(false);
        if (!string.Equals(target.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported remote tool approval notification platform: {target.Platform}");

        var payload = _composer.Compose(
            LarkRemoteToolApprovalCardContent.BuildIntent(notification),
            BuildComposeContext());
        if (!payload.IsInteractive)
            throw new InvalidOperationException("Remote approval notification must render as an interactive Lark card.");

        await _larkSender.SendAsync(
                ChannelDeliveryTargetResolver.ToNativeDeliveryTarget(target),
                new ChannelNativeMessage(null, payload.ContentJson, "interactive", ComposeCapability.Exact),
                ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Delivered remote tool approval card: target={DeliveryTargetId}, request={RequestId}, remote={RemoteApprovalId}",
            deliveryTargetId,
            notification.Request.RequestId,
            notification.Submission.RemoteApprovalId);
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
