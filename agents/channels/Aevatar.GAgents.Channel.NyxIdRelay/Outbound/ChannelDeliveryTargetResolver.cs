using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

public sealed class ChannelDeliveryTargetResolver
{
    private readonly IUserAgentDeliveryTargetReader _deliveryTargetReader;
    private readonly ILogger<ChannelDeliveryTargetResolver> _logger;

    public ChannelDeliveryTargetResolver(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        ILogger<ChannelDeliveryTargetResolver> logger)
    {
        _deliveryTargetReader = deliveryTargetReader ?? throw new ArgumentNullException(nameof(deliveryTargetReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserAgentDeliveryTarget> ResolveAsync(
        string deliveryTargetId,
        string platformSubject,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deliveryTargetId))
            throw new InvalidOperationException($"{platformSubject} delivery target id is required.");

        _logger.LogInformation(
            "Resolving channel delivery target: deliveryTargetId={DeliveryTargetId}, platformSubject={PlatformSubject}",
            deliveryTargetId,
            platformSubject);

        var target = await _deliveryTargetReader.GetAsync(deliveryTargetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            _logger.LogWarning(
                "Channel delivery target resolution failed: deliveryTargetId={DeliveryTargetId}, platformSubject={PlatformSubject}",
                deliveryTargetId,
                platformSubject);
            throw new InvalidOperationException($"Agent delivery target not found: {deliveryTargetId}");
        }

        if (string.IsNullOrWhiteSpace(target.Platform))
            throw new InvalidOperationException($"Agent delivery target platform is missing: {deliveryTargetId}");

        _logger.LogInformation(
            "Resolved channel delivery target: deliveryTargetId={DeliveryTargetId}, platform={Platform}, conversationId={ConversationId}, nyxProviderSlug={NyxProviderSlug}, hasNyxApiKey={HasNyxApiKey}",
            deliveryTargetId,
            target.Platform,
            target.ConversationId,
            target.NyxProviderSlug,
            !string.IsNullOrWhiteSpace(target.NyxApiKey));

        return target;
    }
}
