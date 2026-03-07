using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Application;

public sealed class QueryScriptEvolutionProposalSnapshotRequestAdapter
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

        return ScriptingActorRequestEnvelopeFactory.Create(
            targetActorId,
            requestId,
            new QueryScriptEvolutionProposalSnapshotRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
                ProposalId = proposalId ?? string.Empty,
            },
            QueryPublisherId);
    }
}
