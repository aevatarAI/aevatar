using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Demos.Cli.Messages;

namespace Aevatar.Demos.Cli;

internal sealed class DemoCollectorAgent : GAgentBase<CounterState>
{
    public List<string> Received { get; } = [];

    [EventHandler]
    public Task OnPing(PingEvent evt)
    {
        Received.Add(evt.Message);
        return Task.CompletedTask;
    }

    [EventHandler]
    public Task OnPong(PongEvent evt)
    {
        Received.Add(evt.Reply);
        return Task.CompletedTask;
    }
}
