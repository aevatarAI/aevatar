namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceRolloutSnapshot(
    string ServiceKey,
    string RolloutId,
    string DisplayName,
    string Status,
    int CurrentStageIndex,
    IReadOnlyList<ServiceRolloutStageSnapshot> Stages,
    IReadOnlyList<ServiceServingTargetSnapshot> BaselineTargets,
    string FailureReason,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt);

public sealed record ServiceRolloutStageSnapshot(
    string StageId,
    int StageIndex,
    IReadOnlyList<ServiceServingTargetSnapshot> Targets);
