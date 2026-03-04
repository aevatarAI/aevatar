using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf.WellKnownTypes;

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

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new QueryScriptDefinitionSnapshotRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
                RequestedRevision = requestedRevision ?? string.Empty,
            }),
            PublisherId = QueryPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = targetActorId,
            CorrelationId = requestId,
        };
    }
}
