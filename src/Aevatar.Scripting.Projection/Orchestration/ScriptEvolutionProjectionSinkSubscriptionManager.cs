using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionSinkSubscriptionManager
    : EventSinkProjectionSessionSubscriptionManager<ScriptEvolutionRuntimeLease, ScriptEvolutionSessionCompletedEvent>,
      IScriptEvolutionProjectionSinkSubscriptionManager
{
    public ScriptEvolutionProjectionSinkSubscriptionManager(
        IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent> sessionEventHub)
        : base(sessionEventHub)
    {
    }
}
