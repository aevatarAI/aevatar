using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application;

public sealed class QueryScriptCatalogEntryRequestAdapter
{
    private const string QueryPublisherId = "scripting.query.catalog";

    public EventEnvelope Map(
        string targetActorId,
        string requestId,
        string replyStreamId,
        string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStreamId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new QueryScriptCatalogEntryRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
                ScriptId = scriptId ?? string.Empty,
            }),
            PublisherId = QueryPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = targetActorId,
            CorrelationId = requestId,
        };
    }
}
