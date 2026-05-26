using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// Produces a <see cref="ChannelNativeMessage"/> for the Lark channel by delegating to
/// <see cref="LarkMessageComposer"/> and mapping its <see cref="LarkOutboundMessage"/> shape onto the
/// platform-neutral DTO consumed by cross-platform dispatchers.
/// </summary>
/// <remarks>
/// Keeps the Lark-specific composer as the single owner of native card rendering while
/// exposing a platform-neutral producer for cross-platform relay dispatchers.
/// </remarks>
public sealed class LarkChannelNativeMessageProducer : IChannelNativeMessageProducer
{
    private readonly LarkMessageComposer _composer;

    /// <summary>Initializes a new producer that delegates composition to the supplied Lark composer.</summary>
    public LarkChannelNativeMessageProducer(LarkMessageComposer composer)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
    }

    /// <inheritdoc />
    public ChannelId Channel => _composer.Channel;

    /// <inheritdoc />
    public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) =>
        _composer.Evaluate(intent, context);

    /// <inheritdoc />
    public ChannelNativeMessage Produce(MessageContent intent, ComposeContext context)
    {
        var capability = _composer.Evaluate(intent, context);
        var payload = _composer.Compose(intent, context);

        if (!payload.IsInteractive)
            return new ChannelNativeMessage(
                Text: payload.PlainText,
                CardPayload: null,
                MessageType: payload.MessageType,
                Capability: capability);

        object card = TryParseCard(payload.ContentJson);
        return new ChannelNativeMessage(
            Text: payload.PlainText,
            CardPayload: card,
            MessageType: payload.MessageType,
            Capability: capability);
    }

    private static object TryParseCard(string contentJson)
    {
        try
        {
            using var document = JsonDocument.Parse(contentJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return contentJson;
        }
    }
}
