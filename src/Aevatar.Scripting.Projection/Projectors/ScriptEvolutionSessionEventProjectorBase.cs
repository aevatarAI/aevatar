using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;

namespace Aevatar.Scripting.Projection.Projectors;

public abstract class ScriptEvolutionSessionEventProjectorBase
    : ProjectionSessionEventProjectorBase<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>, ScriptEvolutionSessionCompletedEvent>
{
    protected ScriptEvolutionSessionEventProjectorBase(
        IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent> sessionEventHub)
        : base(sessionEventHub)
    {
    }
}
