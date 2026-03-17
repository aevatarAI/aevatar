using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Governance.Application.Services;

public sealed class ActivationCapabilityViewAssembler : IActivationCapabilityViewReader
{
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceConfigurationQueryReader _configurationQueryReader;
    private readonly IServiceRevisionArtifactStore _artifactStore;

    public ActivationCapabilityViewAssembler(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceConfigurationQueryReader configurationQueryReader,
        IServiceRevisionArtifactStore artifactStore)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _configurationQueryReader = configurationQueryReader ?? throw new ArgumentNullException(nameof(configurationQueryReader));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    }

    public Task<ActivationCapabilityView> GetAsync(
        ServiceIdentity identity,
        string revisionId,
        CancellationToken ct = default) =>
        AssembleAsync(identity, revisionId, ct);

    public async Task<ActivationCapabilityView> AssembleAsync(
        ServiceIdentity identity,
        string revisionId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(revisionId))
            throw new InvalidOperationException("revision_id is required.");

        var serviceKey = ServiceKeys.Build(identity);
        var catalog = await _catalogQueryReader.GetAsync(identity, ct)
            ?? throw new InvalidOperationException($"Service definition '{serviceKey}' was not found.");
        var artifact = await _artifactStore.GetAsync(serviceKey, revisionId, ct)
            ?? throw new InvalidOperationException($"Prepared artifact for '{serviceKey}' revision '{revisionId}' was not found.");
        var configuration = await _configurationQueryReader.GetAsync(identity, ct);

        var view = new ActivationCapabilityView
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        };
        view.Bindings.Add(configuration?.Bindings
            .Where(x => !x.Retired)
            .Select(MapBinding) ?? []);
        view.Endpoints.Add(configuration?.Endpoints.Select(MapEndpoint) ?? []);
        view.Policies.Add(ResolvePolicies(
            catalog.PolicyIds,
            configuration?.Bindings.SelectMany(x => x.PolicyIds) ?? [],
            configuration?.Endpoints.SelectMany(x => x.PolicyIds) ?? [],
            configuration,
            view.MissingPolicyIds));

        foreach (var declaredEndpoint in artifact.Endpoints)
        {
            if (view.Endpoints.Any(x => string.Equals(x.EndpointId, declaredEndpoint.EndpointId, StringComparison.Ordinal)))
                continue;

            view.Endpoints.Add(new ServiceEndpointExposureSpec
            {
                EndpointId = declaredEndpoint.EndpointId,
                DisplayName = declaredEndpoint.DisplayName,
                Kind = declaredEndpoint.Kind,
                RequestTypeUrl = declaredEndpoint.RequestTypeUrl,
                ResponseTypeUrl = declaredEndpoint.ResponseTypeUrl,
                Description = declaredEndpoint.Description,
                ExposureKind = ServiceEndpointExposureKind.Internal,
            });
        }

        return view;
    }

    private static IEnumerable<ServicePolicySpec> ResolvePolicies(
        IEnumerable<string> definitionPolicyIds,
        IEnumerable<string> bindingPolicyIds,
        IEnumerable<string> endpointPolicyIds,
        ServiceConfigurationSnapshot? configuration,
        Google.Protobuf.Collections.RepeatedField<string> missingPolicyIds)
    {
        var referencedPolicyIds = new HashSet<string>(StringComparer.Ordinal);
        AddPolicyIds(definitionPolicyIds, referencedPolicyIds);
        AddPolicyIds(bindingPolicyIds, referencedPolicyIds);
        AddPolicyIds(endpointPolicyIds, referencedPolicyIds);

        foreach (var policyId in referencedPolicyIds)
        {
            var policy = configuration?.Policies.FirstOrDefault(x =>
                string.Equals(x.PolicyId, policyId, StringComparison.Ordinal) &&
                !x.Retired);
            if (policy != null)
            {
                yield return MapPolicy(policy);
                continue;
            }

            missingPolicyIds.Add(policyId);
        }
    }

    private static void AddPolicyIds(IEnumerable<string> values, ISet<string> target)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                target.Add(value);
        }
    }

    private static ServiceBindingSpec MapBinding(ServiceBindingSnapshot snapshot)
    {
        var spec = new ServiceBindingSpec
        {
            BindingId = snapshot.BindingId,
            DisplayName = snapshot.DisplayName,
            BindingKind = snapshot.BindingKind,
        };
        spec.PolicyIds.Add(snapshot.PolicyIds);
        if (snapshot.ServiceRef != null)
        {
            spec.ServiceRef = new BoundServiceRef
            {
                Identity = snapshot.ServiceRef.Identity.Clone(),
                EndpointId = snapshot.ServiceRef.EndpointId ?? string.Empty,
            };
        }
        else if (snapshot.ConnectorRef != null)
        {
            spec.ConnectorRef = new BoundConnectorRef
            {
                ConnectorType = snapshot.ConnectorRef.ConnectorType ?? string.Empty,
                ConnectorId = snapshot.ConnectorRef.ConnectorId ?? string.Empty,
            };
        }
        else if (snapshot.SecretRef != null)
        {
            spec.SecretRef = new BoundSecretRef
            {
                SecretName = snapshot.SecretRef.SecretName,
            };
        }

        return spec;
    }

    private static ServiceEndpointExposureSpec MapEndpoint(ServiceEndpointExposureSnapshot snapshot)
    {
        var spec = new ServiceEndpointExposureSpec
        {
            EndpointId = snapshot.EndpointId,
            DisplayName = snapshot.DisplayName,
            Kind = snapshot.Kind,
            RequestTypeUrl = snapshot.RequestTypeUrl,
            ResponseTypeUrl = snapshot.ResponseTypeUrl,
            Description = snapshot.Description,
            ExposureKind = snapshot.ExposureKind,
        };
        spec.PolicyIds.Add(snapshot.PolicyIds);
        return spec;
    }

    private static ServicePolicySpec MapPolicy(ServicePolicySnapshot snapshot)
    {
        var spec = new ServicePolicySpec
        {
            PolicyId = snapshot.PolicyId,
            DisplayName = snapshot.DisplayName,
            InvokeRequiresActiveDeployment = snapshot.InvokeRequiresActiveDeployment,
        };
        spec.ActivationRequiredBindingIds.Add(snapshot.ActivationRequiredBindingIds);
        spec.InvokeAllowedCallerServiceKeys.Add(snapshot.InvokeAllowedCallerServiceKeys);
        return spec;
    }
}
