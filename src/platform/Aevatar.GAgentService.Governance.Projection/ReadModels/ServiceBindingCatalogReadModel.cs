using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Governance.Projection.ReadModels;

public sealed class ServiceBindingCatalogReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ServiceBindingCatalogReadModel>
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ServiceBindingReadModel> Bindings { get; set; } = [];

    public ServiceBindingCatalogReadModel DeepClone()
    {
        return new ServiceBindingCatalogReadModel
        {
            Id = Id,
            UpdatedAt = UpdatedAt,
            Bindings = Bindings.Select(x => x.DeepClone()).ToList(),
        };
    }
}

public sealed class ServiceBindingReadModel
{
    public string BindingId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string BindingKind { get; set; } = string.Empty;

    public List<string> PolicyIds { get; set; } = [];

    public bool Retired { get; set; }

    public string TargetServiceKey { get; set; } = string.Empty;

    public string TargetEndpointId { get; set; } = string.Empty;

    public string ConnectorType { get; set; } = string.Empty;

    public string ConnectorId { get; set; } = string.Empty;

    public string SecretName { get; set; } = string.Empty;

    public ServiceBindingReadModel DeepClone()
    {
        return new ServiceBindingReadModel
        {
            BindingId = BindingId,
            DisplayName = DisplayName,
            BindingKind = BindingKind,
            PolicyIds = [.. PolicyIds],
            Retired = Retired,
            TargetServiceKey = TargetServiceKey,
            TargetEndpointId = TargetEndpointId,
            ConnectorType = ConnectorType,
            ConnectorId = ConnectorId,
            SecretName = SecretName,
        };
    }
}
