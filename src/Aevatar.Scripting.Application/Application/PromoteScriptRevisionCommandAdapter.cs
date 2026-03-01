using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class PromoteScriptRevisionCommandAdapter
{
    private const string CommandPublisherId = "scripting.application";

    public EventEnvelope Map(PromoteScriptRevisionCommand command, string actorId)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new PromoteScriptRevisionRequestedEvent
            {
                ScriptId = command.ScriptId ?? string.Empty,
                Revision = command.Revision ?? string.Empty,
                DefinitionActorId = command.DefinitionActorId ?? string.Empty,
                SourceHash = command.SourceHash ?? string.Empty,
                ProposalId = command.ProposalId ?? string.Empty,
            }),
            PublisherId = CommandPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = actorId,
            CorrelationId = command.ProposalId ?? string.Empty,
        };
    }
}
