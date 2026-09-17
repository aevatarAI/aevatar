using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// Explicit Feishu boundary mapping for the existing Lark-family outbound composer.
/// Feishu keeps its canonical channel identity while sharing the established card format.
/// </summary>
public sealed class FeishuLarkFamilyMessageComposer : IMessageComposer<LarkOutboundMessage>
{
    private readonly LarkMessageComposer _larkComposer;

    public FeishuLarkFamilyMessageComposer(LarkMessageComposer larkComposer)
    {
        _larkComposer = larkComposer ?? throw new ArgumentNullException(nameof(larkComposer));
    }

    public ChannelId Channel => ChannelId.From("feishu");

    public LarkOutboundMessage Compose(MessageContent intent, ComposeContext context) =>
        _larkComposer.Compose(intent, context);

    object IMessageComposer.Compose(MessageContent intent, ComposeContext context) =>
        Compose(intent, context);

    public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) =>
        _larkComposer.Evaluate(intent, context);
}
