using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

public sealed class NyxIdRelayChannelInteractionNotificationPort : IChannelInteractionNotificationPort
{
    private readonly ChannelDeliveryTargetResolver _targetResolver;
    private readonly IReadOnlyDictionary<string, IChannelNativeMessageProducer> _nativeProducers;
    private readonly IReadOnlyDictionary<string, IChannelNativeMessageSender> _nativeSenders;
    private readonly IReadOnlyDictionary<string, IChannelNativeDeliveryTargetAdapter> _nativeTargetAdapters;
    private readonly ILogger<NyxIdRelayChannelInteractionNotificationPort> _logger;

    public NyxIdRelayChannelInteractionNotificationPort(
        ChannelDeliveryTargetResolver targetResolver,
        IEnumerable<IChannelNativeMessageProducer> nativeProducers,
        IEnumerable<IChannelNativeMessageSender> nativeSenders,
        IEnumerable<IChannelNativeDeliveryTargetAdapter> nativeTargetAdapters,
        ILogger<NyxIdRelayChannelInteractionNotificationPort> logger)
    {
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
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

        var adapters = new Dictionary<string, IChannelNativeDeliveryTargetAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in nativeTargetAdapters ?? [])
        {
            ArgumentNullException.ThrowIfNull(adapter);
            var key = NormalizePlatform(adapter.Channel.Value);
            if (!adapters.TryAdd(key, adapter))
            {
                throw new InvalidOperationException(
                    $"Multiple native delivery target adapters are registered for platform '{key}'.");
            }
        }

        _nativeTargetAdapters = adapters;
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
        var platform = NormalizePlatform(target.Platform);
        if (!_nativeProducers.TryGetValue(platform, out var producer))
            throw new NotSupportedException($"No channel message producer is registered for platform: {target.Platform}");
        if (!_nativeSenders.TryGetValue(platform, out var sender))
            throw new NotSupportedException($"No channel message sender is registered for platform: {target.Platform}");

        var content = HumanInteractionMessageMapper.ToMessageContent(request);
        var nativeMessage = ProduceNativeMessage(producer, content, target);
        var nativeTarget = ToNativeDeliveryTarget(target);
        await sender.SendAsync(nativeTarget, nativeMessage, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Delivered channel interaction notification: target={DeliveryTargetId}, platform={Platform}, run={RunId}, step={StepId}, capability={Capability}",
            request.DeliveryTargetId,
            target.Platform,
            request.RunId,
            request.StepId,
            nativeMessage.Capability);
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

    private ChannelNativeDeliveryTarget ToNativeDeliveryTarget(UserAgentDeliveryTarget target)
    {
        var platform = NormalizePlatform(target.Platform);
        if (_nativeTargetAdapters.TryGetValue(platform, out var adapter))
            return adapter.Adapt(target);

        return new ChannelNativeDeliveryTarget(
            target.AgentId,
            target.Platform,
            target.ConversationId,
            target.NyxProviderSlug,
            target.NyxApiKey);
    }

    private static string NormalizePlatform(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
