using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Lark;

/// <summary>
/// Produces a <see cref="ChannelNativeMessage"/> for the Lark channel by delegating to
/// <see cref="LarkMessageComposer"/> and mapping its <see cref="LarkOutboundMessage"/> shape onto the
/// platform-neutral DTO consumed by cross-platform dispatchers.
/// </summary>
/// <remarks>
/// Keeping the Lark-specific composer unchanged and projecting its output into
/// <see cref="ChannelNativeMessage"/> here preserves the direct-send path used by
/// <c>LarkChannelAdapter</c> (which still depends on <c>IMessageComposer&lt;LarkOutboundMessage&gt;</c>)
/// while exposing a platform-neutral producer for the relay dispatcher.
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
