using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class ProposeScriptEvolutionCommandAdapter
{
    private const string CommandPublisherId = "scripting.application";

    public EventEnvelope Map(ProposeScriptEvolutionCommand command, string actorId)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ProposeScriptEvolutionRequestedEvent
            {
                ProposalId = command.ProposalId ?? string.Empty,
                ScriptId = command.ScriptId ?? string.Empty,
                BaseRevision = command.BaseRevision ?? string.Empty,
                CandidateRevision = command.CandidateRevision ?? string.Empty,
                CandidateSource = command.CandidateSource ?? string.Empty,
                CandidateSourceHash = command.CandidateSourceHash ?? string.Empty,
                Reason = command.Reason ?? string.Empty,
                DefinitionActorId = command.DefinitionActorId ?? string.Empty,
                CatalogActorId = command.CatalogActorId ?? string.Empty,
                RequestedByActorId = command.RequestedByActorId ?? string.Empty,
            }),
            PublisherId = CommandPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = actorId,
            CorrelationId = command.ProposalId ?? string.Empty,
        };
    }
}
