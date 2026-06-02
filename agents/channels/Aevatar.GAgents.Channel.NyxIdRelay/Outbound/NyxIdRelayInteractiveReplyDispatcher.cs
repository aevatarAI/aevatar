using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

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
                BuildTextFallback(intent),
                ComposeCapability.Unspecified,
                cancellationToken);
        }

        var capability = producer.Evaluate(intent, context);
        if (capability == ComposeCapability.Unsupported)
        {
            _logger.LogWarning(
                "Composer rejected interactive intent for channel {Channel}; degrading to text.",
                channel.Value);
            return await SendTextFallbackAsync(relayToken, messageId, BuildTextFallback(intent), capability, cancellationToken);
        }

        if (ShouldUseTextOnlyFallback(channel, intent))
        {
            _logger.LogInformation(
                "Interactive reply dispatcher is degrading action card intent to text for channel {Channel}.",
                channel.Value);
            return await SendTextFallbackAsync(
                relayToken,
                messageId,
                BuildTextFallback(intent),
                capability,
                cancellationToken);
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

    private static bool ShouldUseTextOnlyFallback(
        ChannelId channel,
        MessageContent intent)
    {
        if (!string.Equals(channel.Value, "lark", StringComparison.OrdinalIgnoreCase))
            return false;

        return HasUserVisibleActions(intent.Actions) ||
               intent.Cards.Count > 0;
    }

    private static bool HasUserVisibleActions(IEnumerable<ActionElement> actions) =>
        actions.Any(action => action.Kind != ActionElementKind.TextInput || action.Options.Count > 0);

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

    /// <summary>
    /// Synthesizes a plain-text fallback for an interactive intent by flattening card title/text/fields
    /// and non-input action labels. Used when the downstream channel cannot render a card and the
    /// intent has no top-level <see cref="MessageContent.Text"/> (for example, card-only tool calls).
    /// </summary>
    public static string BuildTextFallback(MessageContent intent)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(intent.Text))
            parts.Add(intent.Text);

        foreach (var card in intent.Cards)
        {
            if (!string.IsNullOrWhiteSpace(card.Title))
                parts.Add(card.Title);
            if (!string.IsNullOrWhiteSpace(card.Text))
                parts.Add(card.Text);

            foreach (var field in card.Fields)
            {
                if (!string.IsNullOrWhiteSpace(field.Title) && !string.IsNullOrWhiteSpace(field.Text))
                    parts.Add($"{field.Title}: {field.Text}");
                else if (!string.IsNullOrWhiteSpace(field.Text))
                    parts.Add(field.Text);
                else if (!string.IsNullOrWhiteSpace(field.Title))
                    parts.Add(field.Title);
            }
        }

        AppendActionLabels(parts, intent.Actions);
        foreach (var card in intent.Cards)
            AppendActionLabels(parts, card.Actions);

        return parts.Count == 0 ? string.Empty : string.Join("\n", parts);
    }

    private static void AppendActionLabels(List<string> parts, IEnumerable<ActionElement> actions)
    {
        foreach (var action in actions)
        {
            if (action.Kind == ActionElementKind.TextInput && action.Options.Count == 0)
                continue;

            var label = !string.IsNullOrWhiteSpace(action.Label)
                ? action.Label
                : action.ActionId;
            if (!string.IsNullOrWhiteSpace(label))
                parts.Add($"• {label}");

            foreach (var option in action.Options)
            {
                var optionLabel = !string.IsNullOrWhiteSpace(option.Label)
                    ? option.Label
                    : option.Value;
                if (!string.IsNullOrWhiteSpace(optionLabel))
                    parts.Add($"  - {optionLabel}");
            }
        }
    }
}
