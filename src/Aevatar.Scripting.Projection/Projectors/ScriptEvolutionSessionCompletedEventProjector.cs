using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptEvolutionSessionCompletedEventProjector
    : ScriptEvolutionSessionEventProjectorBase
{
    public ScriptEvolutionSessionCompletedEventProjector(
        IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<ScriptEvolutionSessionCompletedEvent>> ResolveSessionEventEntries(
        ScriptEvolutionSessionProjectionContext context,
        EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        if (payload == null || !payload.Is(ScriptEvolutionSessionCompletedEvent.Descriptor))
            return EmptyEntries;

        var completed = payload.Unpack<ScriptEvolutionSessionCompletedEvent>();
        var proposalId = string.IsNullOrWhiteSpace(completed.ProposalId)
            ? context.ProposalId
            : completed.ProposalId;
        if (string.IsNullOrWhiteSpace(proposalId))
            return EmptyEntries;

        return
        [
            new ProjectionSessionEventEntry<ScriptEvolutionSessionCompletedEvent>(
                context.RootActorId,
                proposalId,
                completed),
        ];
    }
}
