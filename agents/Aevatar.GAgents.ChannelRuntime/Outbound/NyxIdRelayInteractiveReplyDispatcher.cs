using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime.Outbound;

/// <summary>
/// Dispatches interactive reply intents through the NyxID channel relay transport.
/// </summary>
/// <remarks>
/// For a given channel the dispatcher looks up an <see cref="IChannelNativeMessageProducer"/> via the
/// registry, asks the producer to compose the intent, and assembles the transport body as
/// <c>{ text?, metadata: { card? } }</c>. When no producer is registered for the channel or the composer
/// reports <see cref="ComposeCapability.Unsupported"/>, the dispatcher silently degrades to the intent's
/// plain-text fallback so the end user still receives a message.
/// </remarks>
public sealed class NyxIdRelayInteractiveReplyDispatcher : IInteractiveReplyDispatcher
{
    private readonly IChannelMessageComposerRegistry _registry;
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<NyxIdRelayInteractiveReplyDispatcher> _logger;

    public NyxIdRelayInteractiveReplyDispatcher(
        IChannelMessageComposerRegistry registry,
        NyxIdApiClient nyxClient,
        ILogger<NyxIdRelayInteractiveReplyDispatcher> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<InteractiveReplyDispatchResult> DispatchAsync(
        ChannelId channel,
        string messageId,
        string relayToken,
        MessageContent intent,
        ComposeContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);

        var producer = _registry.GetNativeProducer(channel);
        if (producer is null)
        {
            _logger.LogInformation(
                "Interactive reply dispatcher has no producer for channel {Channel}; degrading to text.",
                channel.Value);
            return await SendTextFallbackAsync(
                relayToken,
                messageId,
                intent.Text,
                ComposeCapability.Unspecified,
                cancellationToken);
        }

        var capability = producer.Evaluate(intent, context);
        if (capability == ComposeCapability.Unsupported)
        {
            _logger.LogWarning(
                "Composer rejected interactive intent for channel {Channel}; degrading to text.",
                channel.Value);
            return await SendTextFallbackAsync(relayToken, messageId, intent.Text, capability, cancellationToken);
        }

        var native = producer.Produce(intent, context);
        var body = new ChannelRelayReplyBody(
            Text: native.Text,
            Metadata: native.CardPayload is null ? null : new ChannelRelayReplyMetadata(native.CardPayload));

        var delivery = await _nyxClient.SendChannelRelayReplyAsync(relayToken, messageId, body, cancellationToken);
        return new InteractiveReplyDispatchResult(
            Succeeded: delivery.Succeeded,
            MessageId: delivery.MessageId,
            PlatformMessageId: delivery.PlatformMessageId,
            Capability: capability,
            FellBackToText: !native.IsInteractive,
            Detail: delivery.Detail);
    }

    private async Task<InteractiveReplyDispatchResult> SendTextFallbackAsync(
        string relayToken,
        string messageId,
        string? text,
        ComposeCapability capability,
        CancellationToken cancellationToken)
    {
        var effectiveText = string.IsNullOrWhiteSpace(text)
            ? "(no content)"
            : text;
        var delivery = await _nyxClient.SendChannelRelayReplyAsync(
            relayToken,
            messageId,
            new ChannelRelayReplyBody(effectiveText),
            cancellationToken);
        return new InteractiveReplyDispatchResult(
            Succeeded: delivery.Succeeded,
            MessageId: delivery.MessageId,
            PlatformMessageId: delivery.PlatformMessageId,
            Capability: capability,
            FellBackToText: true,
            Detail: delivery.Detail);
    }
}
