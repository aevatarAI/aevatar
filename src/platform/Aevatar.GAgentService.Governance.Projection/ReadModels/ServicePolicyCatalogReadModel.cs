using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Governance.Projection.ReadModels;

public sealed class ServicePolicyCatalogReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ServicePolicyCatalogReadModel>
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ServicePolicyReadModel> Policies { get; set; } = [];

    public ServicePolicyCatalogReadModel DeepClone()
    {
        return new ServicePolicyCatalogReadModel
        {
            Id = Id,
            UpdatedAt = UpdatedAt,
            Policies = Policies.Select(x => x.DeepClone()).ToList(),
        };
    }
}

public sealed class ServicePolicyReadModel
{
    public string PolicyId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<string> ActivationRequiredBindingIds { get; set; } = [];

    public List<string> InvokeAllowedCallerServiceKeys { get; set; } = [];

    public bool InvokeRequiresActiveDeployment { get; set; }

    public bool Retired { get; set; }

    public ServicePolicyReadModel DeepClone()
    {
        return new ServicePolicyReadModel
        {
            PolicyId = PolicyId,
            DisplayName = DisplayName,
            ActivationRequiredBindingIds = [.. ActivationRequiredBindingIds],
            InvokeAllowedCallerServiceKeys = [.. InvokeAllowedCallerServiceKeys],
            InvokeRequiresActiveDeployment = InvokeRequiresActiveDeployment,
            Retired = Retired,
        };
    }
}
