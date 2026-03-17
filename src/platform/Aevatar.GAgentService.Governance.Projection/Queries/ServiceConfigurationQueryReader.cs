using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Projection.Configuration;
using Aevatar.GAgentService.Governance.Projection.ReadModels;

namespace Aevatar.GAgentService.Governance.Projection.Queries;

public sealed class ServiceConfigurationQueryReader : IServiceConfigurationQueryReader
{
    private readonly IProjectionDocumentReader<ServiceConfigurationReadModel, string> _documentReader;
    private readonly bool _enabled;

    public ServiceConfigurationQueryReader(
        IProjectionDocumentReader<ServiceConfigurationReadModel, string> documentReader,
        ServiceGovernanceProjectionOptions? options = null)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _enabled = options?.Enabled ?? true;
    }

    public async Task<ServiceConfigurationSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default)
    {
        if (!_enabled)
            return null;

        ArgumentNullException.ThrowIfNull(identity);
        var serviceKey = ServiceKeys.Build(identity);
        var readModel = await _documentReader.GetAsync(serviceKey, ct);
        if (readModel == null)
            return null;

        return new ServiceConfigurationSnapshot(
            readModel.Id,
            ToIdentity(readModel.Identity),
            readModel.Bindings
                .Select(x => new ServiceBindingSnapshot(
                    x.BindingId,
                    x.DisplayName,
                    x.BindingKind,
                    [.. x.PolicyIds],
                    x.Retired,
                    x.ServiceRef == null
                        ? null
                        : new BoundServiceReferenceSnapshot(ToIdentity(x.ServiceRef.Identity), x.ServiceRef.EndpointId),
                    x.ConnectorRef == null
                        ? null
                        : new BoundConnectorReferenceSnapshot(x.ConnectorRef.ConnectorType, x.ConnectorRef.ConnectorId),
                    x.SecretRef == null
                        ? null
                        : new BoundSecretReferenceSnapshot(x.SecretRef.SecretName)))
                .ToList(),
            readModel.Endpoints
                .Select(x => new ServiceEndpointExposureSnapshot(
                    x.EndpointId,
                    x.DisplayName,
                    x.Kind,
                    x.RequestTypeUrl,
                    x.ResponseTypeUrl,
                    x.Description,
                    x.ExposureKind,
                    [.. x.PolicyIds]))
                .ToList(),
            readModel.Policies
                .Select(x => new ServicePolicySnapshot(
                    x.PolicyId,
                    x.DisplayName,
                    [.. x.ActivationRequiredBindingIds],
                    [.. x.InvokeAllowedCallerServiceKeys],
                    x.InvokeRequiresActiveDeployment,
                    x.Retired))
                .ToList(),
            readModel.UpdatedAt);
    }

    private static ServiceIdentity ToIdentity(ServiceIdentityReadModel readModel)
    {
        return new ServiceIdentity
        {
            TenantId = readModel.TenantId ?? string.Empty,
            AppId = readModel.AppId ?? string.Empty,
            Namespace = readModel.Namespace ?? string.Empty,
            ServiceId = readModel.ServiceId ?? string.Empty,
        };
    }
}
