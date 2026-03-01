using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class RollbackScriptRevisionCommandAdapter
{
    private const string CommandPublisherId = "scripting.application";

    public EventEnvelope Map(RollbackScriptRevisionCommand command, string actorId)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new RollbackScriptRevisionRequestedEvent
            {
                ScriptId = command.ScriptId ?? string.Empty,
                TargetRevision = command.TargetRevision ?? string.Empty,
                Reason = command.Reason ?? string.Empty,
                ProposalId = command.ProposalId ?? string.Empty,
            }),
            PublisherId = CommandPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = actorId,
            CorrelationId = command.ProposalId ?? string.Empty,
        };
    }
}
