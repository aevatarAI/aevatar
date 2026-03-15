using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Projection.ReadModels;

public sealed class ServiceServingSetReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ServiceServingSetReadModel>
{
    public string Id { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public long StateVersion { get; set; }

    public string LastEventId { get; set; } = string.Empty;

    public long Generation { get; set; }

    public string ActiveRolloutId { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ServiceServingTargetReadModel> Targets { get; set; } = [];

    public ServiceServingSetReadModel DeepClone()
    {
        return new ServiceServingSetReadModel
        {
            Id = Id,
            ActorId = ActorId,
            StateVersion = StateVersion,
            LastEventId = LastEventId,
            Generation = Generation,
            ActiveRolloutId = ActiveRolloutId,
            UpdatedAt = UpdatedAt,
            Targets = Targets.Select(x => x.DeepClone()).ToList(),
        };
    }
}

public sealed class ServiceServingTargetReadModel
{
    public string DeploymentId { get; set; } = string.Empty;

    public string RevisionId { get; set; } = string.Empty;

    public string PrimaryActorId { get; set; } = string.Empty;

    public int AllocationWeight { get; set; }

    public string ServingState { get; set; } = string.Empty;

    public List<string> EnabledEndpointIds { get; set; } = [];

    public ServiceServingTargetReadModel DeepClone()
    {
        return new ServiceServingTargetReadModel
        {
            DeploymentId = DeploymentId,
            RevisionId = RevisionId,
            PrimaryActorId = PrimaryActorId,
            AllocationWeight = AllocationWeight,
            ServingState = ServingState,
            EnabledEndpointIds = [.. EnabledEndpointIds],
        };
    }
}
