using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

internal static class ScriptActorQueryEnvelopeFactory
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
            Route = EnvelopeRouteSemantics.CreateDirect("scripting-definition-query", targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = requestId,
            },
        };
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
            Route = EnvelopeRouteSemantics.CreateDirect("scripting-catalog-query", targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = requestId,
            },
        };
    }

    public static EventEnvelope CreateBehaviorBindingQuery(
        string targetActorId,
        string requestId,
        string replyStreamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStreamId);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new QueryScriptBehaviorBindingRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("scripting-runtime-binding-query", targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = requestId,
            },
        };
    }
}
