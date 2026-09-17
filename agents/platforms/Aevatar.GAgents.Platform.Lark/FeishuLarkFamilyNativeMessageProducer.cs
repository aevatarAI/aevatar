using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// Explicit Feishu boundary mapping for the existing Lark-family native producer.
/// </summary>
public sealed class FeishuLarkFamilyNativeMessageProducer : IChannelNativeMessageProducer
{
    private readonly LarkChannelNativeMessageProducer _larkProducer;

    public FeishuLarkFamilyNativeMessageProducer(LarkChannelNativeMessageProducer larkProducer)
    {
        _larkProducer = larkProducer ?? throw new ArgumentNullException(nameof(larkProducer));
    }

    public ChannelId Channel => ChannelId.From("feishu");

    public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) =>
        _larkProducer.Evaluate(intent, context);

    public ChannelNativeMessage Produce(MessageContent intent, ComposeContext context) =>
        _larkProducer.Produce(intent, context);
}
