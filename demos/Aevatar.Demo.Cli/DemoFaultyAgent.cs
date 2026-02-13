using Aevatar.Attributes;
using Aevatar.Demo.Messages;

namespace Aevatar.Demo;

internal sealed class DemoFaultyAgent : GAgentBase<CounterState>
{
    [EventHandler]
    public Task OnPing(PingEvent evt) => throw new InvalidOperationException($"fault:{evt.Message}");
}