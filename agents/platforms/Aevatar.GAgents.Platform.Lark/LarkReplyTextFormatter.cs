using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Platform.Lark;

public class LarkReplyTextFormatter : IChannelReplyTextFormatter
{
    public virtual string Platform => "lark";
    public string Format(string text) => LarkJsonTableFormatter.FormatAsKeyValueText(text);
    public bool ContainsStructuredContent(string? text) => LarkJsonTableFormatter.ContainsConvertibleJson(text);
}

/// <summary>Explicit Feishu identity for the existing Lark protocol behavior.</summary>
public sealed class FeishuReplyTextFormatter : LarkReplyTextFormatter
{
    public override string Platform => "feishu";
}
