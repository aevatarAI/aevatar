using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionScopeObservationRelayBinding
{
    private const string LegacyReadinessProbeLeasePrefix = "projection-scope-readiness:";
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
        HasStableRelayShape(binding, rootActorId, targetActorId) &&
        string.Equals(binding!.TargetActorKind, expectedTargetActorKind, StringComparison.Ordinal) &&
        binding.ActivationGeneration > 0;

    public static bool IsLegacyCompatibleActivationEvidence(
        StreamForwardingBinding? binding,
        string rootActorId,
        string targetActorId) =>
        HasStableRelayShape(binding, rootActorId, targetActorId) &&
        string.IsNullOrEmpty(binding!.TargetActorKind) &&
        binding.ActivationGeneration == 0 &&
        binding.Version == 0 &&
        string.IsNullOrEmpty(binding.LeaseId);

    public static StreamForwardingBinding CreateLegacyReadinessProbe(
        string rootActorId,
        string targetActorId)
    {
        var binding = Create(rootActorId, targetActorId, "legacy-readiness-probe", 1);
        binding.TargetActorKind = string.Empty;
        binding.ActivationGeneration = 0;
        binding.LeaseId = $"{LegacyReadinessProbeLeasePrefix}{Guid.NewGuid():N}";
        return binding;
    }

    public static bool IsLegacyReadinessProbe(
        StreamForwardingBinding? binding,
        string rootActorId,
        string targetActorId) =>
        HasStableRelayShape(binding, rootActorId, targetActorId) &&
        string.IsNullOrEmpty(binding!.TargetActorKind) &&
        binding.ActivationGeneration == 0 &&
        binding.Version == 0 &&
        binding.LeaseId?.StartsWith(LegacyReadinessProbeLeasePrefix, StringComparison.Ordinal) == true;

    private static bool HasStableRelayShape(
        StreamForwardingBinding? binding,
        string rootActorId,
        string targetActorId) =>
        binding != null &&
        string.Equals(binding.SourceStreamId, rootActorId, StringComparison.Ordinal) &&
        string.Equals(binding.TargetStreamId, targetActorId, StringComparison.Ordinal) &&
        binding.ForwardingMode == StreamForwardingMode.HandleThenForward &&
        binding.DirectionFilter.Count == 0 &&
        binding.EventTypeFilter.Count == 1 &&
        binding.EventTypeFilter.Contains(CommittedStateTypeUrl);
}
