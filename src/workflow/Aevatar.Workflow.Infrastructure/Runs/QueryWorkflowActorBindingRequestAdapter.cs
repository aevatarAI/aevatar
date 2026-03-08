using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.Runs;

internal sealed class QueryWorkflowActorBindingRequestAdapter
{
    public EventEnvelope Map(
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
            Payload = Any.Pack(new QueryWorkflowActorBindingRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
            }),
            PublisherId = WorkflowQueryChannels.ActorBindingPublisherId,
            Direction = EventDirection.Self,
            TargetActorId = targetActorId,
            CorrelationId = requestId,
        };
    }
}
