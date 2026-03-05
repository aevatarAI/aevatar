using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Application;

public sealed class QueryScriptCatalogEntryRequestAdapter
{
    private const string QueryPublisherId = ScriptingQueryChannels.CatalogPublisherId;

    public EventEnvelope Map(
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
            QueryPublisherId);
    }
}
