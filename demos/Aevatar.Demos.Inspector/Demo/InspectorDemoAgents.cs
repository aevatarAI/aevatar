using Aevatar.Demos.Inspector.Messages;
using Aevatar.Foundation.Abstractions.Attributes;

namespace Aevatar.Demos.Inspector.Demo;

public sealed class InspectorTransformerAgent : GAgentBase<InspectorCounterState>
{
    [EventHandler(Priority = 5)]
    public Task OnPing(InspectorPingEvent evt)
    {
        return PublishAsync(new InspectorPongEvent
        {
            Reply = $"pong:{evt.Message}",
        }, TopologyAudience.Children);
    }
}

public sealed class InspectorCollectorAgent : GAgentBase<InspectorCounterState>
{
    [EventHandler]
    public Task OnPong(InspectorPongEvent evt)
    {
        State.Count += 1;
        return Task.CompletedTask;
    }
}

public sealed class InspectorCounterAgent : GAgentBase<InspectorCounterState>
{
    [EventHandler]
    public Task OnIncrement(InspectorIncrementEvent evt)
    {
        State.Count += evt.Amount;
        return Task.CompletedTask;
    }
}
