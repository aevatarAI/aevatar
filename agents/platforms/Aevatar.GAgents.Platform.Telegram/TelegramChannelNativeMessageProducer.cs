using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Telegram;

/// <summary>
/// Produces a <see cref="ChannelNativeMessage"/> for the Telegram channel by delegating to
/// <see cref="TelegramMessageComposer"/> and mapping its <see cref="TelegramOutboundMessage"/> shape onto the
/// platform-neutral DTO consumed by cross-platform dispatchers.
/// </summary>
public sealed class TelegramChannelNativeMessageProducer : IChannelNativeMessageProducer
{
    private readonly TelegramMessageComposer _composer;

    public TelegramChannelNativeMessageProducer(TelegramMessageComposer composer)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
    }

    public ChannelId Channel => _composer.Channel;

    public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) =>
        _composer.Evaluate(intent, context);

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
