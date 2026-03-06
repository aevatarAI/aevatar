using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Application;

public sealed class QueryScriptEvolutionDecisionRequestAdapter
{
    private const string QueryPublisherId = ScriptingQueryChannels.EvolutionPublisherId;

    public EventEnvelope Map(
        string targetActorId,
        string requestId,
        string replyStreamId,
        string proposalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);

        return ScriptingActorRequestEnvelopeFactory.Create(
            targetActorId,
            proposalId,
            new QueryScriptEvolutionDecisionRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
                ProposalId = proposalId,
            },
            QueryPublisherId);
    }
}
