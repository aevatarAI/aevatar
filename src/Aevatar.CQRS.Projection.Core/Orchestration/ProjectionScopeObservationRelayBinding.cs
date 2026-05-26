using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionScopeObservationRelayBinding
{
    public static StreamForwardingBinding Create(string rootActorId, string targetActorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);

        return new StreamForwardingBinding
        {
            SourceStreamId = rootActorId,
            TargetStreamId = targetActorId,
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [],
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal)
            {
                $"type.googleapis.com/{CommittedStateEventPublished.Descriptor.FullName}",
            },
        };
    }
}
