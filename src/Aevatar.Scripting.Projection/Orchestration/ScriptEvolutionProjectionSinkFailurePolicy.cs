using Aevatar.Scripting.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionSinkFailurePolicy
    : EventSinkProjectionFailurePolicyBase<ScriptEvolutionRuntimeLease, ScriptEvolutionSessionCompletedEvent>,
      IScriptEvolutionProjectionSinkFailurePolicy
{
    public ScriptEvolutionProjectionSinkFailurePolicy(
        IScriptEvolutionProjectionSinkSubscriptionManager sinkSubscriptionManager)
        : base(sinkSubscriptionManager)
    {
    }
}
