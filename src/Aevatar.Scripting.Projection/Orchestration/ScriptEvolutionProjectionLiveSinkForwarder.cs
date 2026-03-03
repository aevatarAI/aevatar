using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionLiveSinkForwarder
    : EventSinkProjectionLiveForwarder<ScriptEvolutionRuntimeLease, ScriptEvolutionSessionCompletedEvent>,
      IScriptEvolutionProjectionLiveSinkForwarder
{
    public ScriptEvolutionProjectionLiveSinkForwarder(
        IScriptEvolutionProjectionSinkFailurePolicy sinkFailurePolicy)
        : base(sinkFailurePolicy)
    {
    }
}
