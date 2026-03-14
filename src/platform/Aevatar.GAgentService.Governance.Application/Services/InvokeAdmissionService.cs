using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Abstractions.Ports;

namespace Aevatar.GAgentService.Governance.Application.Services;

public sealed class InvokeAdmissionService : IInvokeAdmissionAuthorizer
{
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceEndpointCatalogQueryReader _endpointCatalogQueryReader;
    private readonly IServicePolicyQueryReader _policyQueryReader;
    private readonly IInvokeAdmissionEvaluator _evaluator;

    public InvokeAdmissionService(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceEndpointCatalogQueryReader endpointCatalogQueryReader,
        IServicePolicyQueryReader policyQueryReader,
        IInvokeAdmissionEvaluator evaluator)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _endpointCatalogQueryReader = endpointCatalogQueryReader ?? throw new ArgumentNullException(nameof(endpointCatalogQueryReader));
        _policyQueryReader = policyQueryReader ?? throw new ArgumentNullException(nameof(policyQueryReader));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public async Task AuthorizeAsync(
        string serviceKey,
        string deploymentId,
        PreparedServiceRevisionArtifact artifact,
        ServiceEndpointDescriptor endpoint,
        ServiceInvocationRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Identity);

        var catalog = await _catalogQueryReader.GetAsync(request.Identity, ct)
            ?? throw new InvalidOperationException($"Service definition '{serviceKey}' was not found.");
        var endpointCatalog = await _endpointCatalogQueryReader.GetAsync(request.Identity, ct)
            ?? throw new InvalidOperationException($"Endpoint catalog for '{serviceKey}' was not found.");
        var policyCatalog = await _policyQueryReader.GetAsync(request.Identity, ct);
        var endpointEntry = endpointCatalog.Endpoints.FirstOrDefault(x =>
            string.Equals(x.EndpointId, request.EndpointId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Endpoint '{request.EndpointId}' was not published for service '{serviceKey}'.");

        var admissionRequest = new InvokeAdmissionRequest
        {
            Identity = request.Identity.Clone(),
            ServiceKey = serviceKey,
            EndpointId = request.EndpointId ?? string.Empty,
            Endpoint = new ServiceEndpointExposureSpec
            {
                EndpointId = endpointEntry.EndpointId,
                DisplayName = endpointEntry.DisplayName,
                Kind = Enum.TryParse<ServiceEndpointKind>(endpointEntry.Kind, out var kind) ? kind : ServiceEndpointKind.Unspecified,
                RequestTypeUrl = endpointEntry.RequestTypeUrl,
                ResponseTypeUrl = endpointEntry.ResponseTypeUrl,
                Description = endpointEntry.Description,
                ExposureKind = Enum.TryParse<ServiceEndpointExposureKind>(endpointEntry.ExposureKind, out var exposureKind) ? exposureKind : ServiceEndpointExposureKind.Unspecified,
                PolicyIds = { endpointEntry.PolicyIds },
            },
            HasActiveDeployment = !string.IsNullOrWhiteSpace(deploymentId),
            Caller = request.Caller?.Clone() ?? new ServiceInvocationCaller(),
        };

        var referencedPolicyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policyId in catalog.PolicyIds)
        {
            if (!string.IsNullOrWhiteSpace(policyId))
                referencedPolicyIds.Add(policyId);
        }

        foreach (var policyId in endpointEntry.PolicyIds)
        {
            if (!string.IsNullOrWhiteSpace(policyId))
                referencedPolicyIds.Add(policyId);
        }

        foreach (var policyId in referencedPolicyIds)
        {
            var policy = policyCatalog?.Policies.FirstOrDefault(x =>
                string.Equals(x.PolicyId, policyId, StringComparison.Ordinal) &&
                !x.Retired);
            if (policy != null)
            {
                admissionRequest.Policies.Add(new ServicePolicySpec
                {
                    PolicyId = policy.PolicyId,
                    DisplayName = policy.DisplayName,
                    ActivationRequiredBindingIds = { policy.ActivationRequiredBindingIds },
                    InvokeAllowedCallerServiceKeys = { policy.InvokeAllowedCallerServiceKeys },
                    InvokeRequiresActiveDeployment = policy.InvokeRequiresActiveDeployment,
                });
                continue;
            }

            admissionRequest.MissingPolicyIds.Add(policyId);
        }

        var decision = await _evaluator.EvaluateAsync(admissionRequest, ct);
        if (decision.Allowed)
            return;

        var reason = decision.Violations.Count == 0
            ? "invoke admission rejected."
            : string.Join("; ", decision.Violations.Select(x => $"{x.Code}:{x.SubjectId}:{x.Message}"));
        throw new InvalidOperationException(reason);
    }
}
