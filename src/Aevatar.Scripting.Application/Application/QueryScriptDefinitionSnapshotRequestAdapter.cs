using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Application;

public sealed class QueryScriptDefinitionSnapshotRequestAdapter
{
    private const string QueryPublisherId = ScriptingQueryChannels.DefinitionPublisherId;

    public EventEnvelope Map(
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
            QueryPublisherId);
    }
}
