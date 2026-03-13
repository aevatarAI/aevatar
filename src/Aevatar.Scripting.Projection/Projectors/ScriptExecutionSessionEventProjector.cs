using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptExecutionSessionEventProjector
    : ProjectionSessionEventProjectorBase<ScriptExecutionProjectionContext, IReadOnlyList<string>, EventEnvelope>
{
    public ScriptExecutionSessionEventProjector(
        IProjectionSessionEventHub<EventEnvelope> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<EventEnvelope>> ResolveSessionEventEntries(
        ScriptExecutionProjectionContext context,
        EventEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(context.RootActorId))
            return EmptyEntries;

        return
        [
            new ProjectionSessionEventEntry<EventEnvelope>(
                context.RootActorId,
                context.RootActorId,
                envelope)
        ];
    }
}
