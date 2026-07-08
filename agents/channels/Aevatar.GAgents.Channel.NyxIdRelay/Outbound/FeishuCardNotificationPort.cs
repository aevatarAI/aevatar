using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Platform.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

public sealed class FeishuCardNotificationPort : IChannelInteractionNotificationPort
{
    private readonly ChannelDeliveryTargetResolver _targetResolver;
    private readonly LarkMessageComposer _composer;
    private readonly LarkChannelNativeMessageSender _larkSender;
    private readonly ILogger<FeishuCardNotificationPort> _logger;

    public FeishuCardNotificationPort(
        ChannelDeliveryTargetResolver targetResolver,
        LarkMessageComposer composer,
        LarkChannelNativeMessageSender larkSender,
        ILogger<FeishuCardNotificationPort> logger)
    {
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _larkSender = larkSender ?? throw new ArgumentNullException(nameof(larkSender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task DeliverAsync(
        ChannelInteractionNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await _targetResolver.ResolveAsync(
                request.DeliveryTargetId,
                "interaction notification",
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(target.Platform, "lark", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported interaction notification platform: {target.Platform}");

        await _larkSender.SendAsync(
                ChannelDeliveryTargetResolver.ToNativeDeliveryTarget(target),
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
}
