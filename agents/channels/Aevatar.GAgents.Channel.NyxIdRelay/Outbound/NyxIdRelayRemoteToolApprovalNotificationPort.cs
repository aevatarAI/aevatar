using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

public sealed class NyxIdRelayRemoteToolApprovalNotificationPort : IRemoteToolApprovalNotificationPort
{
    private const string SupportedApprovalPlatform = "lark";

    private readonly ChannelDeliveryTargetResolver _targetResolver;
    private readonly IReadOnlyDictionary<string, IChannelNativeMessageProducer> _nativeProducers;
    private readonly IReadOnlyDictionary<string, IChannelNativeMessageSender> _nativeSenders;
    private readonly IReadOnlyDictionary<string, IChannelNativeDeliveryTargetAdapter> _nativeTargetAdapters;
    private readonly ILogger<NyxIdRelayRemoteToolApprovalNotificationPort> _logger;

    public NyxIdRelayRemoteToolApprovalNotificationPort(
        ChannelDeliveryTargetResolver targetResolver,
        IEnumerable<IChannelNativeMessageProducer> nativeProducers,
        IEnumerable<IChannelNativeMessageSender> nativeSenders,
        IEnumerable<IChannelNativeDeliveryTargetAdapter> nativeTargetAdapters,
        ILogger<NyxIdRelayRemoteToolApprovalNotificationPort> logger)
    {
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        ArgumentNullException.ThrowIfNull(nativeProducers);
        ArgumentNullException.ThrowIfNull(nativeSenders);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _nativeProducers = BuildUniquePlatformMap(
            nativeProducers,
            static producer => producer.Channel.Value,
            "native message producers");
        _nativeSenders = BuildUniquePlatformMap(
            nativeSenders,
            static sender => sender.Channel.Value,
            "native message senders");
        _nativeTargetAdapters = BuildUniquePlatformMap(
            nativeTargetAdapters ?? [],
            static adapter => adapter.Channel.Value,
            "native delivery target adapters");
    }

    public async Task<RemoteToolApprovalNotificationSupport> CheckSupportAsync(
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        var deliveryTargetId = NormalizeOptional(toolContext.Channel.DeliveryTargetId);
        if (deliveryTargetId is null)
        {
            return RemoteToolApprovalNotificationSupport.Unsupported(
                "Remote approval notification requires an explicit delivery target id.");
        }

        UserAgentDeliveryTarget target;
        try
        {
            target = await _targetResolver.ResolveAsync(
                    deliveryTargetId,
                    "remote tool approval notification",
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return RemoteToolApprovalNotificationSupport.Unsupported(ex.Message);
        }

        var platform = NormalizePlatform(target.Platform);
        if (platform != SupportedApprovalPlatform)
        {
            return RemoteToolApprovalNotificationSupport.Unsupported(
                $"Remote tool approval notification is currently supported only for Lark delivery targets; platform '{target.Platform}' is not supported.");
        }

        if (!_nativeProducers.ContainsKey(platform))
            return RemoteToolApprovalNotificationSupport.Unsupported($"No channel message producer is registered for platform: {target.Platform}");
        if (!_nativeSenders.ContainsKey(platform))
            return RemoteToolApprovalNotificationSupport.Unsupported($"No channel message sender is registered for platform: {target.Platform}");

        return RemoteToolApprovalNotificationSupport.SupportedResult;
    }

    public async Task NotifyAsync(RemoteToolApprovalNotification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var deliveryTargetId = NormalizeOptional(notification.ToolContext.Channel.DeliveryTargetId);
        if (deliveryTargetId is null)
            throw new InvalidOperationException("Remote approval notification requires an explicit delivery target id.");

        var target = await _targetResolver.ResolveAsync(
                deliveryTargetId,
                "remote tool approval notification",
                ct)
            .ConfigureAwait(false);
        var platform = NormalizePlatform(target.Platform);
        if (platform != SupportedApprovalPlatform)
        {
            throw new NotSupportedException(
                $"Remote tool approval notification is currently supported only for Lark delivery targets; platform '{target.Platform}' is not supported.");
        }

        if (!_nativeProducers.TryGetValue(platform, out var producer))
            throw new NotSupportedException($"No channel message producer is registered for platform: {target.Platform}");
        if (!_nativeSenders.TryGetValue(platform, out var sender))
            throw new NotSupportedException($"No channel message sender is registered for platform: {target.Platform}");

        var content = RemoteToolApprovalMessageMapper.ToMessageContent(notification);
        var nativeMessage = ProduceNativeMessage(producer, content, target);
        var nativeTarget = ToNativeDeliveryTarget(target, platform);
        await sender.SendAsync(nativeTarget, nativeMessage, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Delivered remote tool approval notification: target={DeliveryTargetId}, platform={Platform}, request={RequestId}, remote={RemoteApprovalId}, capability={Capability}",
            deliveryTargetId,
            target.Platform,
            notification.Request.RequestId,
            notification.Submission.RemoteApprovalId,
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
                $"Channel producer for platform '{target.Platform}' cannot express the requested remote tool approval notification.");
        }

        var nativeMessage = producer.Produce(content, context);
        if (nativeMessage.Capability == ComposeCapability.Unsupported)
        {
            throw new NotSupportedException(
                $"Channel producer for platform '{target.Platform}' produced an unsupported remote tool approval notification.");
        }

        if (!nativeMessage.IsInteractive)
            throw new InvalidOperationException("Remote approval notification must render as an interactive channel message.");

        return nativeMessage;
    }

    private ChannelNativeDeliveryTarget ToNativeDeliveryTarget(UserAgentDeliveryTarget target, string platform)
    {
        if (_nativeTargetAdapters.TryGetValue(platform, out var adapter))
            return adapter.Adapt(target);

        return new ChannelNativeDeliveryTarget(
            target.AgentId,
            target.Platform,
            target.ConversationId,
            target.NyxProviderSlug,
            target.NyxApiKey);
    }

    private static IReadOnlyDictionary<string, T> BuildUniquePlatformMap<T>(
        IEnumerable<T> services,
        Func<T, string> getPlatform,
        string serviceDescription)
    {
        var map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services)
        {
            ArgumentNullException.ThrowIfNull(service);
            var key = NormalizePlatform(getPlatform(service));
            if (!map.TryAdd(key, service))
            {
                throw new InvalidOperationException(
                    $"Multiple {serviceDescription} are registered for platform '{key}'.");
            }
        }

        return map;
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string NormalizePlatform(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
