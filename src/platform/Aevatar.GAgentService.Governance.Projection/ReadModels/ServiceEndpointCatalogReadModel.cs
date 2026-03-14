using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Governance.Projection.ReadModels;

public sealed class ServiceEndpointCatalogReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ServiceEndpointCatalogReadModel>
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ServiceEndpointExposureReadModel> Endpoints { get; set; } = [];

    public ServiceEndpointCatalogReadModel DeepClone()
    {
        return new ServiceEndpointCatalogReadModel
        {
            Id = Id,
            UpdatedAt = UpdatedAt,
            Endpoints = Endpoints.Select(x => x.DeepClone()).ToList(),
        };
    }
}

public sealed class ServiceEndpointExposureReadModel
{
    public string EndpointId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string RequestTypeUrl { get; set; } = string.Empty;

    public string ResponseTypeUrl { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string ExposureKind { get; set; } = string.Empty;

    public List<string> PolicyIds { get; set; } = [];

    public ServiceEndpointExposureReadModel DeepClone()
    {
        return new ServiceEndpointExposureReadModel
        {
            EndpointId = EndpointId,
            DisplayName = DisplayName,
            Kind = Kind,
            RequestTypeUrl = RequestTypeUrl,
            ResponseTypeUrl = ResponseTypeUrl,
            Description = Description,
            ExposureKind = ExposureKind,
            PolicyIds = [.. PolicyIds],
        };
    }
}
