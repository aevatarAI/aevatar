using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Demo.Messages;

namespace Aevatar.Demo;

internal sealed class DemoTransformerAgent : GAgentBase<CounterState>
{
    [EventHandler(Priority = 5)]
    public Task OnPing(PingEvent evt) => PublishAsync(new PongEvent { Reply = $"pong:{evt.Message}" }, EventDirection.Down);
}