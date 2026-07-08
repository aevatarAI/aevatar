using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

public sealed class FeishuCardNotificationPort : IChannelInteractionNotificationPort
{
    private readonly IUserAgentDeliveryTargetReader _deliveryTargetReader;
    private readonly LarkMessageComposer _composer;
    private readonly LarkChannelNativeMessageSender _larkSender;
    private readonly IChannelNativeDeliveryTargetAdapter _targetAdapter;
    private readonly ILogger<FeishuCardNotificationPort> _logger;

    public FeishuCardNotificationPort(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        LarkMessageComposer composer,
        LarkChannelNativeMessageSender larkSender,
        ILogger<FeishuCardNotificationPort> logger,
        IChannelNativeDeliveryTargetAdapter? targetAdapter = null)
    {
        _deliveryTargetReader = deliveryTargetReader ?? throw new ArgumentNullException(nameof(deliveryTargetReader));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _larkSender = larkSender ?? throw new ArgumentNullException(nameof(larkSender));
        _targetAdapter = targetAdapter ?? new LarkChannelNativeDeliveryTargetAdapter();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task DeliverAsync(
        ChannelInteractionNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await ResolveAsync(
                request.DeliveryTargetId,
                "interaction notification",
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(target.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported interaction notification platform: {target.Platform}");

        await _larkSender.SendAsync(
                _targetAdapter.Adapt(ToNativeDeliveryTarget(target)),
                new ChannelNativeMessage(
                    Text: null,
                    CardPayload: LarkInteractionCardRenderer.BuildCardJson(request, _composer),
                    MessageType: "interactive",
                    Capability: ComposeCapability.Exact),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Delivered interaction notification card: target={DeliveryTargetId}, run={RunId}, step={StepId}",
            request.DeliveryTargetId,
            request.RunId,
            request.StepId);
    }

    private async Task<UserAgentDeliveryTarget> ResolveAsync(
        string deliveryTargetId,
        string platformSubject,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deliveryTargetId))
            throw new InvalidOperationException($"{platformSubject} delivery target id is required.");

        var target = await _deliveryTargetReader.GetAsync(deliveryTargetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
            throw new InvalidOperationException($"Agent delivery target not found: {deliveryTargetId}");
        if (string.IsNullOrWhiteSpace(target.Platform))
            throw new InvalidOperationException($"Agent delivery target platform is missing: {deliveryTargetId}");

        return target;
    }

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
