using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

public sealed class NyxIdRelayChannelInteractionNotificationPort : IChannelInteractionNotificationPort
{
    private readonly IUserAgentDeliveryTargetReader _deliveryTargetReader;
    private readonly IReadOnlyDictionary<string, IChannelNativeMessageProducer> _nativeProducers;
    private readonly IReadOnlyDictionary<string, IChannelNativeMessageSender> _nativeSenders;
    private readonly ILogger<NyxIdRelayChannelInteractionNotificationPort> _logger;

    public NyxIdRelayChannelInteractionNotificationPort(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        IEnumerable<IChannelNativeMessageProducer> nativeProducers,
        IEnumerable<IChannelNativeMessageSender> nativeSenders,
        ILogger<NyxIdRelayChannelInteractionNotificationPort> logger)
    {
        _deliveryTargetReader = deliveryTargetReader ?? throw new ArgumentNullException(nameof(deliveryTargetReader));
        ArgumentNullException.ThrowIfNull(nativeProducers);
        ArgumentNullException.ThrowIfNull(nativeSenders);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var producers = new Dictionary<string, IChannelNativeMessageProducer>(StringComparer.OrdinalIgnoreCase);
        foreach (var producer in nativeProducers)
        {
            ArgumentNullException.ThrowIfNull(producer);
            var key = NormalizePlatform(producer.Channel.Value);
            if (!producers.TryAdd(key, producer))
            {
                throw new InvalidOperationException(
                    $"Multiple native message producers are registered for platform '{key}'.");
            }
        }

        _nativeProducers = producers;

        var senders = new Dictionary<string, IChannelNativeMessageSender>(StringComparer.OrdinalIgnoreCase);
        foreach (var sender in nativeSenders)
        {
            ArgumentNullException.ThrowIfNull(sender);
            var key = NormalizePlatform(sender.Channel.Value);
            if (!senders.TryAdd(key, sender))
            {
                throw new InvalidOperationException(
                    $"Multiple native message senders are registered for platform '{key}'.");
            }
        }

        _nativeSenders = senders;
    }

    public async Task DeliverAsync(
        ChannelInteractionNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await ResolveTargetAsync(request.DeliveryTargetId, cancellationToken).ConfigureAwait(false);
        var platform = NormalizePlatform(target.Platform);
        if (!_nativeProducers.TryGetValue(platform, out var producer))
            throw new NotSupportedException($"No channel message producer is registered for platform: {target.Platform}");
        if (!_nativeSenders.TryGetValue(platform, out var sender))
            throw new NotSupportedException($"No channel message sender is registered for platform: {target.Platform}");

        var content = HumanInteractionMessageMapper.ToMessageContent(request);
        var nativeMessage = ProduceNativeMessage(producer, content, target);
        await sender.SendAsync(target, nativeMessage, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Delivered channel interaction notification: target={DeliveryTargetId}, platform={Platform}, run={RunId}, step={StepId}, capability={Capability}",
            request.DeliveryTargetId,
            target.Platform,
            request.RunId,
            request.StepId,
            nativeMessage.Capability);
    }

    private async Task<UserAgentDeliveryTarget> ResolveTargetAsync(
        string deliveryTargetId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deliveryTargetId))
            throw new InvalidOperationException("Interaction notification delivery target id is required.");

        var target = await _deliveryTargetReader.GetAsync(deliveryTargetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
            throw new InvalidOperationException($"Agent delivery target not found: {deliveryTargetId}");

        if (string.IsNullOrWhiteSpace(target.Platform))
            throw new InvalidOperationException($"Agent delivery target platform is missing: {deliveryTargetId}");

        return target;
    }

    private static ChannelNativeMessage ProduceNativeMessage(
        IChannelNativeMessageProducer producer,
        MessageContent content,
        UserAgentDeliveryTarget target)
    {
        var context = new ComposeContext
        {
            Conversation = new ConversationReference
            {
                CanonicalKey = $"{NormalizePlatform(target.Platform)}:{target.ConversationId}",
            },
        };
        var capability = producer.Evaluate(content, context);
        if (capability == ComposeCapability.Unsupported)
        {
            throw new NotSupportedException(
                $"Channel producer for platform '{target.Platform}' cannot express the requested interaction notification.");
        }

        var nativeMessage = producer.Produce(content, context);
        if (nativeMessage.Capability == ComposeCapability.Unsupported)
        {
            throw new NotSupportedException(
                $"Channel producer for platform '{target.Platform}' produced an unsupported interaction notification.");
        }

        return nativeMessage;
    }

    private static string NormalizePlatform(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
