using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Projection.ReadModels;

public sealed class ServiceTrafficViewReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ServiceTrafficViewReadModel>
{
    public string Id { get; set; } = string.Empty;

    public long Generation { get; set; }

    public string ActiveRolloutId { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ServiceTrafficEndpointReadModel> Endpoints { get; set; } = [];

    public ServiceTrafficViewReadModel DeepClone()
    {
        return new ServiceTrafficViewReadModel
        {
            Id = Id,
            Generation = Generation,
            ActiveRolloutId = ActiveRolloutId,
            UpdatedAt = UpdatedAt,
            Endpoints = Endpoints.Select(x => x.DeepClone()).ToList(),
        };
    }
}

public sealed class ServiceTrafficEndpointReadModel
{
    public string EndpointId { get; set; } = string.Empty;

    public List<ServiceTrafficTargetReadModel> Targets { get; set; } = [];

    public ServiceTrafficEndpointReadModel DeepClone()
    {
        return new ServiceTrafficEndpointReadModel
        {
            EndpointId = EndpointId,
            Targets = Targets.Select(x => x.DeepClone()).ToList(),
        };
    }
}

public sealed class ServiceTrafficTargetReadModel
{
    public string DeploymentId { get; set; } = string.Empty;

    public string RevisionId { get; set; } = string.Empty;

    public string PrimaryActorId { get; set; } = string.Empty;

    public int AllocationWeight { get; set; }

    public string ServingState { get; set; } = string.Empty;

    public ServiceTrafficTargetReadModel DeepClone()
    {
        return new ServiceTrafficTargetReadModel
        {
            DeploymentId = DeploymentId,
            RevisionId = RevisionId,
            PrimaryActorId = PrimaryActorId,
            AllocationWeight = AllocationWeight,
            ServingState = ServingState,
        };
    }
}
