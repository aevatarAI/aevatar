using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.GAgentService.Governance.Hosting.Endpoints;

internal static class ServicePolicyEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{serviceId}/policies", HandleCreatePolicyAsync);
        group.MapPut("/{serviceId}/policies/{policyId}", HandleUpdatePolicyAsync);
        group.MapPost("/{serviceId}/policies/{policyId}:retire", HandleRetirePolicyAsync);
        group.MapGet("/{serviceId}/policies", HandleGetPoliciesAsync);
        group.MapGet("/{serviceId}:activation-capability", HandleGetActivationCapabilityAsync);
    }

    private static async Task<IResult> HandleCreatePolicyAsync(
        string serviceId,
        ServicePolicyHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.CreatePolicyAsync(new CreateServicePolicyCommand
        {
            Spec = ToSpec(serviceId, request, request.PolicyId ?? string.Empty),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/policies/{request.PolicyId}", receipt);
    }

    private static async Task<IResult> HandleUpdatePolicyAsync(
        string serviceId,
        string policyId,
        ServicePolicyHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.UpdatePolicyAsync(new UpdateServicePolicyCommand
        {
            Spec = ToSpec(serviceId, request, policyId),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/policies/{policyId}", receipt);
    }

    private static async Task<IResult> HandleRetirePolicyAsync(
        string serviceId,
        string policyId,
        GAgentServiceGovernanceEndpointModels.ServiceIdentityHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.RetirePolicyAsync(new RetireServicePolicyCommand
        {
            Identity = GAgentServiceGovernanceEndpointModels.ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            PolicyId = policyId,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/policies/{policyId}", receipt);
    }

    private static Task<ServicePolicyCatalogSnapshot?> HandleGetPoliciesAsync(
        string serviceId,
        [AsParameters] GAgentServiceGovernanceEndpointModels.ServiceIdentityQuery query,
        [FromServices] IServiceGovernanceQueryPort queryPort,
        CancellationToken ct) =>
        queryPort.GetPoliciesAsync(
            GAgentServiceGovernanceEndpointModels.ToIdentity(query.TenantId, query.AppId, query.Namespace, serviceId),
            ct);

    private static Task<ActivationCapabilityView> HandleGetActivationCapabilityAsync(
        string serviceId,
        [AsParameters] ActivationCapabilityQuery query,
        [FromServices] IActivationCapabilityViewReader capabilityViewReader,
        CancellationToken ct) =>
        capabilityViewReader.GetAsync(
            GAgentServiceGovernanceEndpointModels.ToIdentity(query.TenantId, query.AppId, query.Namespace, serviceId),
            query.RevisionId ?? string.Empty,
            ct);

    private static ServicePolicySpec ToSpec(string serviceId, ServicePolicyHttpRequest request, string policyId)
    {
        var spec = new ServicePolicySpec
        {
            Identity = GAgentServiceGovernanceEndpointModels.ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            PolicyId = policyId,
            DisplayName = request.DisplayName ?? string.Empty,
            InvokeRequiresActiveDeployment = request.InvokeRequiresActiveDeployment,
        };
        spec.ActivationRequiredBindingIds.Add(request.ActivationRequiredBindingIds ?? []);
        spec.InvokeAllowedCallerServiceKeys.Add(request.InvokeAllowedCallerServiceKeys ?? []);
        return spec;
    }

    public sealed record ServicePolicyHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        string? PolicyId,
        string DisplayName,
        IReadOnlyList<string>? ActivationRequiredBindingIds,
        IReadOnlyList<string>? InvokeAllowedCallerServiceKeys,
        bool InvokeRequiresActiveDeployment);

    public sealed record ActivationCapabilityQuery(
        string? TenantId,
        string? AppId,
        string? Namespace,
        string? RevisionId);
}
