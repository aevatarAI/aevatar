using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Projection.ReadModels;

public sealed class ServiceDeploymentCatalogReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ServiceDeploymentCatalogReadModel>
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ServiceDeploymentReadModel> Deployments { get; set; } = [];

    public ServiceDeploymentCatalogReadModel DeepClone()
    {
        return new ServiceDeploymentCatalogReadModel
        {
            Id = Id,
            UpdatedAt = UpdatedAt,
            Deployments = Deployments.Select(x => x.DeepClone()).ToList(),
        };
    }
}

public sealed class ServiceDeploymentReadModel
{
    public string DeploymentId { get; set; } = string.Empty;

    public string RevisionId { get; set; } = string.Empty;

    public string PrimaryActorId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTimeOffset? ActivatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ServiceDeploymentReadModel DeepClone()
    {
        return new ServiceDeploymentReadModel
        {
            DeploymentId = DeploymentId,
            RevisionId = RevisionId,
            PrimaryActorId = PrimaryActorId,
            Status = Status,
            ActivatedAt = ActivatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
