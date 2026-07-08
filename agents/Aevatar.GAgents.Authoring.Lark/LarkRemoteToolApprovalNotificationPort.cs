using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Authoring.Lark;

public sealed class LarkRemoteToolApprovalNotificationPort : IRemoteToolApprovalNotificationPort
{
    private readonly IUserAgentDeliveryTargetReader _deliveryTargetReader;
    private readonly LarkMessageComposer _composer;
    private readonly LarkChannelNativeMessageSender _larkSender;
    private readonly IChannelNativeDeliveryTargetAdapter _targetAdapter;
    private readonly ILogger<LarkRemoteToolApprovalNotificationPort> _logger;

    public LarkRemoteToolApprovalNotificationPort(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        LarkMessageComposer composer,
        LarkChannelNativeMessageSender larkSender,
        ILogger<LarkRemoteToolApprovalNotificationPort> logger,
        IChannelNativeDeliveryTargetAdapter? targetAdapter = null)
    {
        _deliveryTargetReader = deliveryTargetReader ?? throw new ArgumentNullException(nameof(deliveryTargetReader));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _larkSender = larkSender ?? throw new ArgumentNullException(nameof(larkSender));
        _targetAdapter = targetAdapter ?? new LarkChannelNativeDeliveryTargetAdapter();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task NotifyAsync(RemoteToolApprovalNotification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var deliveryTargetId = Normalize(notification.ToolContext.Channel.DeliveryTargetId);
        if (deliveryTargetId is null)
            throw new InvalidOperationException("Remote approval notification requires an explicit delivery target id.");

        var target = await _deliveryTargetReader.GetAsync(deliveryTargetId, ct).ConfigureAwait(false);
        if (target is null)
            throw new InvalidOperationException($"Agent delivery target not found: {deliveryTargetId}");
        if (!string.Equals(target.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported remote tool approval notification platform: {target.Platform}");

        var payload = _composer.Compose(
            LarkRemoteToolApprovalCardContent.BuildIntent(notification),
            BuildComposeContext());
        if (!payload.IsInteractive)
            throw new InvalidOperationException("Remote approval notification must render as an interactive Lark card.");

        await _larkSender.SendAsync(
                _targetAdapter.Adapt(ToNativeDeliveryTarget(target)),
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

    private static ChannelNativeDeliveryTarget ToNativeDeliveryTarget(UserAgentDeliveryTarget target) =>
        new RoutedChannelNativeDeliveryTarget(
            target.AgentId,
            target.Platform,
            target.ConversationId,
            target.NyxProviderSlug,
            target.NyxApiKey,
            target.LarkReceiveId,
            target.LarkReceiveIdType,
            target.LarkReceiveIdFallback,
            target.LarkReceiveIdTypeFallback);

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private sealed record RoutedChannelNativeDeliveryTarget(
        string AgentId,
        string Platform,
        string ConversationId,
        string NyxProviderSlug,
        string NyxApiKey,
        string LarkReceiveId,
        string LarkReceiveIdType,
        string LarkReceiveIdFallback,
        string LarkReceiveIdTypeFallback)
        : ChannelNativeDeliveryTarget(
            AgentId,
            Platform,
            ConversationId,
            NyxProviderSlug,
            NyxApiKey);
}
