using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class PromoteScriptRevisionActorRequestAdapter
{
    private const string RequestPublisherId = "scripting.application";

    public EventEnvelope Map(PromoteScriptRevisionActorRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new PromoteScriptRevisionRequestedEvent
            {
                ScriptId = request.ScriptId ?? string.Empty,
                Revision = request.Revision ?? string.Empty,
                DefinitionActorId = request.DefinitionActorId ?? string.Empty,
                SourceHash = request.SourceHash ?? string.Empty,
                ProposalId = request.ProposalId ?? string.Empty,
                ExpectedBaseRevision = request.ExpectedBaseRevision ?? string.Empty,
            }),
            PublisherId = RequestPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = actorId,
            CorrelationId = request.ProposalId ?? string.Empty,
        };
    }
}
