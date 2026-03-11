using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Application;

public static class ScriptingQueryEnvelopeFactory
{
    public static EventEnvelope CreateDefinitionSnapshotQuery(
        string targetActorId,
        string requestId,
        string replyStreamId,
        string requestedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStreamId);

        return ScriptingActorRequestEnvelopeFactory.Create(
            targetActorId,
            requestId,
            new QueryScriptDefinitionSnapshotRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
                RequestedRevision = requestedRevision ?? string.Empty,
            },
            ScriptingQueryChannels.DefinitionPublisherId);
    }

    public static EventEnvelope CreateCatalogEntryQuery(
        string targetActorId,
        string requestId,
        string replyStreamId,
        string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStreamId);

        return ScriptingActorRequestEnvelopeFactory.Create(
            targetActorId,
            requestId,
            new QueryScriptCatalogEntryRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
                ScriptId = scriptId ?? string.Empty,
            },
            ScriptingQueryChannels.CatalogPublisherId);
    }

    public static EventEnvelope CreateEvolutionDecisionQuery(
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
            ScriptingQueryChannels.EvolutionPublisherId);
    }
}
