using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Projection.ReadModels;

public sealed class ServiceCatalogReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ServiceCatalogReadModel>
{
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string AppId { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string ServiceId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string DefaultServingRevisionId { get; set; } = string.Empty;

    public string ActiveServingRevisionId { get; set; } = string.Empty;

    public string DeploymentId { get; set; } = string.Empty;

    public string PrimaryActorId { get; set; } = string.Empty;

    public string DeploymentStatus { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ServiceCatalogEndpointReadModel> Endpoints { get; set; } = [];

    public List<string> PolicyIds { get; set; } = [];

    public ServiceCatalogReadModel DeepClone()
    {
        return new ServiceCatalogReadModel
        {
            Id = Id,
            TenantId = TenantId,
            AppId = AppId,
            Namespace = Namespace,
            ServiceId = ServiceId,
            DisplayName = DisplayName,
            DefaultServingRevisionId = DefaultServingRevisionId,
            ActiveServingRevisionId = ActiveServingRevisionId,
            DeploymentId = DeploymentId,
            PrimaryActorId = PrimaryActorId,
            DeploymentStatus = DeploymentStatus,
            UpdatedAt = UpdatedAt,
            Endpoints = Endpoints
                .Select(x => x.DeepClone())
                .ToList(),
            PolicyIds = [.. PolicyIds],
        };
    }
}

public sealed class ServiceCatalogEndpointReadModel
{
    public string EndpointId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string RequestTypeUrl { get; set; } = string.Empty;

    public string ResponseTypeUrl { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public ServiceCatalogEndpointReadModel DeepClone()
    {
        return new ServiceCatalogEndpointReadModel
        {
            EndpointId = EndpointId,
            DisplayName = DisplayName,
            Kind = Kind,
            RequestTypeUrl = RequestTypeUrl,
            ResponseTypeUrl = ResponseTypeUrl,
            Description = Description,
        };
    }
}
