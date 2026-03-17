using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Demos.Cli.Messages;

namespace Aevatar.Demos.Cli;

internal sealed class DemoTransformerAgent : GAgentBase<CounterState>
{
    private string _replyPrefix = "pong";

    [EventHandler]
    public Task OnSetReply(SetTransformerReplyEvent evt)
    {
        _replyPrefix = string.IsNullOrWhiteSpace(evt.Reply) ? "pong" : evt.Reply.Trim();
        return Task.CompletedTask;
    }

    [EventHandler(Priority = 5)]
    public Task OnPing(PingEvent evt) => PublishAsync(new PongEvent { Reply = $"{_replyPrefix}:{evt.Message}" }, TopologyAudience.Children);
}
