using Aevatar.Attributes;
using Aevatar.Demo.Messages;

namespace Aevatar.Demo;

internal sealed class DemoCounterAgent : GAgentBase<CounterState>
{
    [EventHandler]
    public Task OnIncrement(IncrementEvent evt)
    {
        var before = State.Count;
        State.Count += evt.Amount;
        DemoAgentBase.RecordState(Id, "count", before.ToString(), State.Count.ToString());
        return Task.CompletedTask;
    }
}