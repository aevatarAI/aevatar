using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Infrastructure.Runs;

internal static class WorkflowActorBindingQueryEnvelopeFactory
{
    public static EventEnvelope Create(
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
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(new QueryWorkflowActorBindingRequestedEvent
            {
                RequestId = requestId,
                ReplyStreamId = replyStreamId,
            }),
            Route = new EnvelopeRoute
            {
                PublisherActorId = WorkflowQueryChannels.ActorBindingPublisherId,
                Direction = EventDirection.Self,
                TargetActorId = targetActorId,
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = requestId,
            },
        };
    }
}
