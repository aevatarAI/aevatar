using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

public interface ISkillRunnerOutboundDeliveryPort
{
    Task<SkillRunnerOutboundDeliveryReceipt> SendAsync(
        SkillRunnerOutboundDeliveryRequest request,
        CancellationToken ct);

    Task<SkillRunnerOutboundDeliveryReceipt> UpdateAsync(
        SkillRunnerOutboundDeliveryRequest request,
        string platformMessageId,
        bool isFinal,
        CancellationToken ct);
}

public sealed record SkillRunnerOutboundDeliveryRequest(
    string AgentId,
    SkillRunnerOutboundConfig? OutboundConfig,
    string Text,
    SkillRunnerOutboundDeliveryStyle Style,
    string? ProviderSlugOverride = null);

public enum SkillRunnerOutboundDeliveryStyle
{
    Text,
    Card,
}

public sealed record SkillRunnerOutboundDeliveryReceipt(
    string SentActivityId,
    string PlatformMessageId,
    ComposeCapability Capability);

internal sealed class SkillRunnerOutboundUpdateSealedException : InvalidOperationException
{
    public SkillRunnerOutboundUpdateSealedException(string message)
        : base(message)
    {
    }
}

internal sealed class ChannelNativeSkillRunnerOutboundDeliveryPort : ISkillRunnerOutboundDeliveryPort
{
    private const string MessageUpdateSealedErrorCode = "message_update_sealed";

    private readonly IUserAgentDeliveryTargetReader? _targetReader;
    private readonly IReadOnlyDictionary<string, IChannelNativeMessageProducer> _nativeProducers;
    private readonly IReadOnlyDictionary<string, IChannelNativeMessageSender> _nativeSenders;
    private readonly IReadOnlyDictionary<string, IChannelNativeDeliveryTargetAdapter> _nativeTargetAdapters;
    private readonly ILogger<ChannelNativeSkillRunnerOutboundDeliveryPort> _logger;

    public ChannelNativeSkillRunnerOutboundDeliveryPort(
        IEnumerable<IChannelNativeMessageProducer> nativeProducers,
        IEnumerable<IChannelNativeMessageSender> nativeSenders,
        IEnumerable<IChannelNativeDeliveryTargetAdapter> nativeTargetAdapters,
        ILogger<ChannelNativeSkillRunnerOutboundDeliveryPort> logger,
        IUserAgentDeliveryTargetReader? targetReader = null)
    {
        ArgumentNullException.ThrowIfNull(nativeProducers);
        ArgumentNullException.ThrowIfNull(nativeSenders);
        ArgumentNullException.ThrowIfNull(nativeTargetAdapters);

        _targetReader = targetReader;
        _nativeProducers = BuildUniqueLookup(nativeProducers, producer => producer.Channel.Value, "native message producers");
        _nativeSenders = BuildUniqueLookup(nativeSenders, sender => sender.Channel.Value, "native message senders");
        _nativeTargetAdapters = BuildUniqueLookup(nativeTargetAdapters, adapter => adapter.Channel.Value, "native delivery target adapters");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SkillRunnerOutboundDeliveryReceipt> SendAsync(
        SkillRunnerOutboundDeliveryRequest request,
        CancellationToken ct)
    {
        return await DeliverAsync(
            request,
            platformMessageId: null,
            isFinal: false,
            ct).ConfigureAwait(false);
    }

    public async Task<SkillRunnerOutboundDeliveryReceipt> UpdateAsync(
        SkillRunnerOutboundDeliveryRequest request,
        string platformMessageId,
        bool isFinal,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(platformMessageId))
            throw new InvalidOperationException("SkillRunner streaming update requires a platform message id.");

        return await DeliverAsync(
            request,
            platformMessageId.Trim(),
            isFinal,
            ct).ConfigureAwait(false);
    }

    private async Task<SkillRunnerOutboundDeliveryReceipt> DeliverAsync(
        SkillRunnerOutboundDeliveryRequest request,
        string? platformMessageId,
        bool isFinal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new InvalidOperationException("SkillRunner outbound delivery requires non-empty text.");

        var target = await ResolveTargetAsync(request, ct).ConfigureAwait(false);
        var platform = NormalizePlatform(target.Platform);
        if (!_nativeProducers.TryGetValue(platform, out var producer))
            throw new NotSupportedException($"No channel message producer is registered for platform: {target.Platform}");
        if (!_nativeSenders.TryGetValue(platform, out var sender))
            throw new NotSupportedException($"No channel message sender is registered for platform: {target.Platform}");

        var content = BuildMessageContent(request);
        var context = new ComposeContext
        {
            Conversation = new ConversationReference
            {
                CanonicalKey = $"{platform}:{target.ConversationId}",
            },
        };

        var capability = producer.Evaluate(content, context);
        if (capability == ComposeCapability.Unsupported)
        {
            throw new NotSupportedException(
                $"Channel producer for platform '{target.Platform}' cannot express SkillRunner outbound delivery.");
        }

        var nativeMessage = producer.Produce(content, context);
        if (nativeMessage.Capability == ComposeCapability.Unsupported)
        {
            throw new NotSupportedException(
                $"Channel producer for platform '{target.Platform}' produced unsupported SkillRunner outbound delivery.");
        }

        var nativeTarget = ToNativeDeliveryTarget(target);
        var result = string.IsNullOrWhiteSpace(platformMessageId)
            ? await sender.SendAsync(nativeTarget, nativeMessage, ct).ConfigureAwait(false)
            : await sender.UpdateAsync(nativeTarget, platformMessageId, nativeMessage, isFinal, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorCode, MessageUpdateSealedErrorCode, StringComparison.Ordinal))
            {
                throw new SkillRunnerOutboundUpdateSealedException(
                    string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? "SkillRunner streaming update target is sealed."
                        : result.ErrorMessage);
            }

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? $"SkillRunner outbound delivery failed: {result.ErrorCode}"
                    : $"SkillRunner outbound delivery failed: {result.ErrorMessage}");
        }

        _logger.LogInformation(
            "Delivered SkillRunner outbound message: agent={AgentId}, platform={Platform}, capability={Capability}, style={Style}, update={Update}, final={Final}",
            target.AgentId,
            target.Platform,
            result.Capability,
            request.Style,
            !string.IsNullOrWhiteSpace(platformMessageId),
            isFinal);

        return new SkillRunnerOutboundDeliveryReceipt(
            result.SentActivityId,
            string.IsNullOrWhiteSpace(result.PlatformMessageId)
                ? result.SentActivityId
                : result.PlatformMessageId,
            result.Capability);
    }

    private async Task<UserAgentDeliveryTarget> ResolveTargetAsync(
        SkillRunnerOutboundDeliveryRequest request,
        CancellationToken ct)
    {
        var target = _targetReader is null
            ? null
            : await _targetReader.GetAsync(request.AgentId, ct).ConfigureAwait(false);

        target ??= BuildTargetFromState(request.AgentId, request.OutboundConfig);
        if (target is null)
            throw new InvalidOperationException($"SkillRunner outbound delivery target is unavailable: {request.AgentId}");

        if (!string.IsNullOrWhiteSpace(request.ProviderSlugOverride))
        {
            target = target with { NyxProviderSlug = request.ProviderSlugOverride.Trim() };
        }

        if (string.IsNullOrWhiteSpace(target.Platform))
            throw new InvalidOperationException($"SkillRunner outbound delivery target platform is missing: {request.AgentId}");
        if (string.IsNullOrWhiteSpace(target.ConversationId))
            throw new InvalidOperationException($"SkillRunner outbound delivery target conversation id is missing: {request.AgentId}");
        if (string.IsNullOrWhiteSpace(target.NyxProviderSlug))
            throw new InvalidOperationException($"SkillRunner outbound delivery target provider slug is missing: {request.AgentId}");
        if (string.IsNullOrWhiteSpace(target.NyxApiKey))
            throw new InvalidOperationException($"SkillRunner outbound delivery target credential is missing: {request.AgentId}");

        return target;
    }

    private static UserAgentDeliveryTarget? BuildTargetFromState(
        string agentId,
        SkillRunnerOutboundConfig? outbound)
    {
        if (outbound is null ||
            string.IsNullOrWhiteSpace(outbound.NyxApiKey) ||
            string.IsNullOrWhiteSpace(outbound.NyxProviderSlug) ||
            string.IsNullOrWhiteSpace(outbound.ConversationId))
        {
            return null;
        }

        return new UserAgentDeliveryTarget(
            AgentId: agentId,
            Platform: ResolvePlatform(outbound),
            ConversationId: outbound.ConversationId,
            NyxProviderSlug: outbound.NyxProviderSlug,
            NyxApiKey: outbound.NyxApiKey,
            ChannelAddress: UserAgentCatalogChannelAddress.ToModel(
                null,
                ResolvePlatform(outbound),
                outbound.NyxProviderSlug,
                outbound.ConversationId,
                outbound.LarkReceiveId,
                outbound.LarkReceiveIdType,
                outbound.LarkReceiveIdFallback,
                outbound.LarkReceiveIdTypeFallback),
            OutputFormat: outbound.OutputFormat,
            TemplateName: string.Empty,
            AgentType: string.Empty);
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

    private static MessageContent BuildMessageContent(SkillRunnerOutboundDeliveryRequest request)
    {
        var content = new MessageContent
        {
            Text = request.Style == SkillRunnerOutboundDeliveryStyle.Card
                ? "Scheduled run output"
                : request.Text,
        };

        if (request.Style == SkillRunnerOutboundDeliveryStyle.Card)
        {
            content.Cards.Add(new CardBlock
            {
                Kind = CardBlockKind.Section,
                Title = "Scheduled run output",
                Text = request.Text,
            });
        }

        return content;
    }

    private static string ResolvePlatform(SkillRunnerOutboundConfig outbound)
    {
        if (!string.IsNullOrWhiteSpace(outbound.OwnerScope?.Platform))
            return outbound.OwnerScope.Platform.Trim();

        if (!string.IsNullOrWhiteSpace(outbound.Platform))
            return outbound.Platform.Trim();

        return SkillRunnerDefaults.DefaultPlatform;
    }

    private static IReadOnlyDictionary<string, T> BuildUniqueLookup<T>(
        IEnumerable<T> values,
        Func<T, string?> keySelector,
        string subject)
        where T : class
    {
        var lookup = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var key = NormalizePlatform(keySelector(value));
            if (!lookup.TryAdd(key, value))
                throw new InvalidOperationException($"Multiple {subject} are registered for platform '{key}'.");
        }

        return lookup;
    }

    private static string NormalizePlatform(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
