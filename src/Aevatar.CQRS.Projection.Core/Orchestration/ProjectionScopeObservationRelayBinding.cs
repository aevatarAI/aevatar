using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionScopeObservationRelayBinding
{
    private static readonly string CommittedStateTypeUrl =
        $"type.googleapis.com/{CommittedStateEventPublished.Descriptor.FullName}";

    public static StreamForwardingBinding Create(
        string rootActorId,
        string targetActorId,
        string targetActorKind,
        long activationGeneration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorKind);

        return new StreamForwardingBinding
        {
            SourceStreamId = rootActorId,
            TargetStreamId = targetActorId,
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [],
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal)
            {
                CommittedStateTypeUrl,
            },
            TargetActorKind = targetActorKind,
            ActivationGeneration = activationGeneration,
        };
    }

    public static bool IsExactActivationEvidence(
        StreamForwardingBinding? binding,
        string rootActorId,
        string targetActorId,
        string expectedTargetActorKind) =>
        binding != null &&
        string.Equals(binding.SourceStreamId, rootActorId, StringComparison.Ordinal) &&
        string.Equals(binding.TargetStreamId, targetActorId, StringComparison.Ordinal) &&
        binding.ForwardingMode == StreamForwardingMode.HandleThenForward &&
        binding.DirectionFilter.Count == 0 &&
        binding.EventTypeFilter.Count == 1 &&
        binding.EventTypeFilter.Contains(CommittedStateTypeUrl) &&
        string.Equals(binding.TargetActorKind, expectedTargetActorKind, StringComparison.Ordinal) &&
        binding.ActivationGeneration > 0;
}
