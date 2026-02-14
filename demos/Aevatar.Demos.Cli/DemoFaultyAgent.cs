using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Demos.Cli.Messages;

namespace Aevatar.Demos.Cli;

internal sealed class DemoFaultyAgent : GAgentBase<CounterState>
{
    [EventHandler]
    public Task OnPing(PingEvent evt) => throw new InvalidOperationException($"fault:{evt.Message}");
}
