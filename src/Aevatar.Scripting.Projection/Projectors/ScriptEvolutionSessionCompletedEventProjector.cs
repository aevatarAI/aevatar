using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;

namespace Aevatar.Scripting.Projection.Projectors;

public sealed class ScriptEvolutionSessionCompletedEventProjector
    : ProjectionSessionEventProjectorBase<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>, ScriptEvolutionSessionCompletedEvent>
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
        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload == null ||
            !payload.Is(ScriptEvolutionSessionCompletedEvent.Descriptor))
        {
            return EmptyEntries;
        }

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
