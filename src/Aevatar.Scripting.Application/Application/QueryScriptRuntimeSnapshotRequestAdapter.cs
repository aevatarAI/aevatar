using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Application;

public sealed class QueryScriptRuntimeSnapshotRequestAdapter
{
    private const string QueryPublisherId = ScriptingQueryChannels.RuntimePublisherId;

    public EventEnvelope Map(
        string targetActorId,
        string requestId,
        string replyStreamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStreamId);

        return ScriptingActorRequestEnvelopeFactory.Create(
            targetActorId,
            requestId,
            new QueryScriptRuntimeSnapshotRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
            },
            QueryPublisherId);
    }
}
