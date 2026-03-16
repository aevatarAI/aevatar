using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Projection.ReadModels;

public sealed class ServiceRolloutReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ServiceRolloutReadModel>
{
    public string Id { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public long StateVersion { get; set; }

    public string LastEventId { get; set; } = string.Empty;

    public string RolloutId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int CurrentStageIndex { get; set; }

    public string FailureReason { get; set; } = string.Empty;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ServiceRolloutStageReadModel> Stages { get; set; } = [];

    public List<ServiceServingTargetReadModel> BaselineTargets { get; set; } = [];

    public ServiceRolloutReadModel DeepClone()
    {
        return new ServiceRolloutReadModel
        {
            Id = Id,
            ActorId = ActorId,
            StateVersion = StateVersion,
            LastEventId = LastEventId,
            RolloutId = RolloutId,
            DisplayName = DisplayName,
            Status = Status,
            CurrentStageIndex = CurrentStageIndex,
            FailureReason = FailureReason,
            StartedAt = StartedAt,
            UpdatedAt = UpdatedAt,
            Stages = Stages.Select(x => x.DeepClone()).ToList(),
            BaselineTargets = BaselineTargets.Select(x => x.DeepClone()).ToList(),
        };
    }
}

public sealed class ServiceRolloutStageReadModel
{
    public string StageId { get; set; } = string.Empty;

    public int StageIndex { get; set; }

    public List<ServiceServingTargetReadModel> Targets { get; set; } = [];

    public ServiceRolloutStageReadModel DeepClone()
    {
        return new ServiceRolloutStageReadModel
        {
            StageId = StageId,
            StageIndex = StageIndex,
            Targets = Targets.Select(x => x.DeepClone()).ToList(),
        };
    }
}
