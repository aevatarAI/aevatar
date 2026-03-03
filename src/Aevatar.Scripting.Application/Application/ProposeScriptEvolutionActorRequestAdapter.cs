using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class ProposeScriptEvolutionActorRequestAdapter
{
    private const string RequestPublisherId = "scripting.application";

    public EventEnvelope Map(ProposeScriptEvolutionActorRequest request, string actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ProposeScriptEvolutionRequestedEvent
            {
                ProposalId = request.ProposalId ?? string.Empty,
                ScriptId = request.ScriptId ?? string.Empty,
                BaseRevision = request.BaseRevision ?? string.Empty,
                CandidateRevision = request.CandidateRevision ?? string.Empty,
                CandidateSource = request.CandidateSource ?? string.Empty,
                CandidateSourceHash = request.CandidateSourceHash ?? string.Empty,
                Reason = request.Reason ?? string.Empty,
                DecisionRequestId = request.DecisionRequestId ?? string.Empty,
                DecisionReplyStreamId = request.DecisionReplyStreamId ?? string.Empty,
            }),
            PublisherId = RequestPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = actorId,
            CorrelationId = request.ProposalId ?? string.Empty,
        };
    }
}
