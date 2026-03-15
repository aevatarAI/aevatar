using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;

namespace Aevatar.GAgentService.Governance.Projection.ReadModels;

public sealed class ServiceConfigurationReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ServiceConfigurationReadModel>
{
    public string Id { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public long StateVersion { get; set; }

    public string LastEventId { get; set; } = string.Empty;

    public ServiceIdentityReadModel Identity { get; set; } = new();

    public DateTimeOffset UpdatedAt { get; set; }

    public List<ServiceBindingReadModel> Bindings { get; set; } = [];

    public List<ServiceEndpointExposureReadModel> Endpoints { get; set; } = [];

    public List<ServicePolicyReadModel> Policies { get; set; } = [];

    public ServiceConfigurationReadModel DeepClone()
    {
        return new ServiceConfigurationReadModel
        {
            Id = Id,
            ActorId = ActorId,
            StateVersion = StateVersion,
            LastEventId = LastEventId,
            Identity = Identity.DeepClone(),
            UpdatedAt = UpdatedAt,
            Bindings = Bindings.Select(x => x.DeepClone()).ToList(),
            Endpoints = Endpoints.Select(x => x.DeepClone()).ToList(),
            Policies = Policies.Select(x => x.DeepClone()).ToList(),
        };
    }
}

public sealed class ServiceIdentityReadModel
{
    public string TenantId { get; set; } = string.Empty;

    public string AppId { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string ServiceId { get; set; } = string.Empty;

    public ServiceIdentityReadModel DeepClone()
    {
        return new ServiceIdentityReadModel
        {
            TenantId = TenantId,
            AppId = AppId,
            Namespace = Namespace,
            ServiceId = ServiceId,
        };
    }
}

public sealed class BoundServiceReferenceReadModel
{
    public ServiceIdentityReadModel Identity { get; set; } = new();

    public string EndpointId { get; set; } = string.Empty;

    public BoundServiceReferenceReadModel DeepClone()
    {
        return new BoundServiceReferenceReadModel
        {
            Identity = Identity.DeepClone(),
            EndpointId = EndpointId,
        };
    }
}

public sealed class BoundConnectorReferenceReadModel
{
    public string ConnectorType { get; set; } = string.Empty;

    public string ConnectorId { get; set; } = string.Empty;

    public BoundConnectorReferenceReadModel DeepClone()
    {
        return new BoundConnectorReferenceReadModel
        {
            ConnectorType = ConnectorType,
            ConnectorId = ConnectorId,
        };
    }
}

public sealed class BoundSecretReferenceReadModel
{
    public string SecretName { get; set; } = string.Empty;

    public BoundSecretReferenceReadModel DeepClone()
    {
        return new BoundSecretReferenceReadModel
        {
            SecretName = SecretName,
        };
    }
}

public sealed class ServiceBindingReadModel
{
    public string BindingId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ServiceBindingKind BindingKind { get; set; }

    public List<string> PolicyIds { get; set; } = [];

    public bool Retired { get; set; }

    public BoundServiceReferenceReadModel? ServiceRef { get; set; }

    public BoundConnectorReferenceReadModel? ConnectorRef { get; set; }

    public BoundSecretReferenceReadModel? SecretRef { get; set; }

    public ServiceBindingReadModel DeepClone()
    {
        return new ServiceBindingReadModel
        {
            BindingId = BindingId,
            DisplayName = DisplayName,
            BindingKind = BindingKind,
            PolicyIds = [.. PolicyIds],
            Retired = Retired,
            ServiceRef = ServiceRef?.DeepClone(),
            ConnectorRef = ConnectorRef?.DeepClone(),
            SecretRef = SecretRef?.DeepClone(),
        };
    }
}

public sealed class ServiceEndpointExposureReadModel
{
    public string EndpointId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ServiceEndpointKind Kind { get; set; }

    public string RequestTypeUrl { get; set; } = string.Empty;

    public string ResponseTypeUrl { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public ServiceEndpointExposureKind ExposureKind { get; set; }

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
