using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class RollbackScriptRevisionActorRequestAdapter
{
    private const string RequestPublisherId = "scripting.application";

    public EventEnvelope Map(RollbackScriptRevisionActorRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new RollbackScriptRevisionRequestedEvent
            {
                ScriptId = request.ScriptId ?? string.Empty,
                TargetRevision = request.TargetRevision ?? string.Empty,
                Reason = request.Reason ?? string.Empty,
                ProposalId = request.ProposalId ?? string.Empty,
                ExpectedCurrentRevision = request.ExpectedCurrentRevision ?? string.Empty,
            }),
            PublisherId = RequestPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = actorId,
            CorrelationId = request.ProposalId ?? string.Empty,
        };
    }
}
