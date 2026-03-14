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
    private readonly IServiceBindingQueryReader _bindingQueryReader;
    private readonly IServiceEndpointCatalogQueryReader _endpointCatalogQueryReader;
    private readonly IServicePolicyQueryReader _policyQueryReader;
    private readonly IServiceRevisionArtifactStore _artifactStore;

    public ActivationCapabilityViewAssembler(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceBindingQueryReader bindingQueryReader,
        IServiceEndpointCatalogQueryReader endpointCatalogQueryReader,
        IServicePolicyQueryReader policyQueryReader,
        IServiceRevisionArtifactStore artifactStore)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _bindingQueryReader = bindingQueryReader ?? throw new ArgumentNullException(nameof(bindingQueryReader));
        _endpointCatalogQueryReader = endpointCatalogQueryReader ?? throw new ArgumentNullException(nameof(endpointCatalogQueryReader));
        _policyQueryReader = policyQueryReader ?? throw new ArgumentNullException(nameof(policyQueryReader));
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
        var bindingCatalog = await _bindingQueryReader.GetAsync(identity, ct);
        var endpointCatalog = await _endpointCatalogQueryReader.GetAsync(identity, ct);
        var policyCatalog = await _policyQueryReader.GetAsync(identity, ct);

        var view = new ActivationCapabilityView
        {
            Identity = identity.Clone(),
            RevisionId = revisionId,
        };
        view.Bindings.Add(bindingCatalog?.Bindings
            .Where(x => !x.Retired)
            .Select(MapBinding) ?? []);
        view.Endpoints.Add(endpointCatalog?.Endpoints.Select(MapEndpoint) ?? []);
        view.Policies.Add(ResolvePolicies(
            catalog.PolicyIds,
            bindingCatalog?.Bindings.SelectMany(x => x.PolicyIds) ?? [],
            endpointCatalog?.Endpoints.SelectMany(x => x.PolicyIds) ?? [],
            policyCatalog,
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
        ServicePolicyCatalogSnapshot? policyCatalog,
        Google.Protobuf.Collections.RepeatedField<string> missingPolicyIds)
    {
        var referencedPolicyIds = new HashSet<string>(StringComparer.Ordinal);
        AddPolicyIds(definitionPolicyIds, referencedPolicyIds);
        AddPolicyIds(bindingPolicyIds, referencedPolicyIds);
        AddPolicyIds(endpointPolicyIds, referencedPolicyIds);

        foreach (var policyId in referencedPolicyIds)
        {
            var policy = policyCatalog?.Policies.FirstOrDefault(x =>
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
            BindingKind = Enum.TryParse<ServiceBindingKind>(snapshot.BindingKind, out var bindingKind)
                ? bindingKind
                : ServiceBindingKind.Unspecified,
        };
        spec.PolicyIds.Add(snapshot.PolicyIds);
        if (!string.IsNullOrWhiteSpace(snapshot.TargetServiceKey))
        {
            var parts = snapshot.TargetServiceKey.Split(':', StringSplitOptions.None);
            if (parts.Length == 4)
            {
                spec.ServiceRef = new BoundServiceRef
                {
                    Identity = new ServiceIdentity
                    {
                        TenantId = parts[0],
                        AppId = parts[1],
                        Namespace = parts[2],
                        ServiceId = parts[3],
                    },
                    EndpointId = snapshot.TargetEndpointId ?? string.Empty,
                };
            }
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.ConnectorType) || !string.IsNullOrWhiteSpace(snapshot.ConnectorId))
        {
            spec.ConnectorRef = new BoundConnectorRef
            {
                ConnectorType = snapshot.ConnectorType ?? string.Empty,
                ConnectorId = snapshot.ConnectorId ?? string.Empty,
            };
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.SecretName))
        {
            spec.SecretRef = new BoundSecretRef
            {
                SecretName = snapshot.SecretName,
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
            Kind = Enum.TryParse<ServiceEndpointKind>(snapshot.Kind, out var kind)
                ? kind
                : ServiceEndpointKind.Unspecified,
            RequestTypeUrl = snapshot.RequestTypeUrl,
            ResponseTypeUrl = snapshot.ResponseTypeUrl,
            Description = snapshot.Description,
            ExposureKind = Enum.TryParse<ServiceEndpointExposureKind>(snapshot.ExposureKind, out var exposureKind)
                ? exposureKind
                : ServiceEndpointExposureKind.Unspecified,
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
