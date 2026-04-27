using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Hosting.Identity;
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
        HttpContext http,
        string serviceId,
        ServicePolicyHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        if (ServiceIdentityEndpointAccess.TryResolveContext(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                out _,
                out var denied) == false)
        {
            return denied;
        }

        var receipt = await commandPort.CreatePolicyAsync(new CreateServicePolicyCommand
        {
            Spec = ToSpec(serviceId, request, request.PolicyId ?? string.Empty, identityResolver),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/policies/{request.PolicyId}", receipt);
    }

    private static async Task<IResult> HandleUpdatePolicyAsync(
        HttpContext http,
        string serviceId,
        string policyId,
        ServicePolicyHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        if (ServiceIdentityEndpointAccess.TryResolveContext(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                out _,
                out var denied) == false)
        {
            return denied;
        }

        var receipt = await commandPort.UpdatePolicyAsync(new UpdateServicePolicyCommand
        {
            Spec = ToSpec(serviceId, request, policyId, identityResolver),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/policies/{policyId}", receipt);
    }

    private static async Task<IResult> HandleRetirePolicyAsync(
        HttpContext http,
        string serviceId,
        string policyId,
        GAgentServiceGovernanceEndpointModels.ServiceIdentityHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var receipt = await commandPort.RetirePolicyAsync(new RetireServicePolicyCommand
        {
            Identity = identity,
            PolicyId = policyId,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/policies/{policyId}", receipt);
    }

    private static async Task<IResult> HandleGetPoliciesAsync(
        HttpContext http,
        string serviceId,
        [AsParameters] GAgentServiceGovernanceEndpointModels.ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceGovernanceQueryPort queryPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                query.TenantId,
                query.AppId,
                query.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        return JsonOrNull(await queryPort.GetPoliciesAsync(identity, ct));
    }

    private static async Task<IResult> HandleGetActivationCapabilityAsync(
        HttpContext http,
        string serviceId,
        [AsParameters] ActivationCapabilityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IActivationCapabilityViewReader capabilityViewReader,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                query.TenantId,
                query.AppId,
                query.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        return JsonOrNull(await capabilityViewReader.GetAsync(identity, query.RevisionId ?? string.Empty, ct));
    }

    private static ServicePolicySpec ToSpec(
        string serviceId,
        ServicePolicyHttpRequest request,
        string policyId,
        IServiceIdentityContextResolver identityResolver)
    {
        var context = identityResolver.Resolve() ?? new ServiceIdentityContext(
            request.TenantId?.Trim() ?? string.Empty,
            request.AppId?.Trim() ?? string.Empty,
            request.Namespace?.Trim() ?? string.Empty,
            "request");
        var spec = new ServicePolicySpec
        {
            Identity = GAgentServiceGovernanceEndpointModels.ToIdentity(context.TenantId, context.AppId, context.Namespace, serviceId),
            PolicyId = policyId,
            DisplayName = request.DisplayName ?? string.Empty,
            InvokeRequiresActiveDeployment = request.InvokeRequiresActiveDeployment,
        };
        spec.ActivationRequiredBindingIds.Add(request.ActivationRequiredBindingIds ?? []);
        spec.InvokeAllowedCallerServiceKeys.Add(request.InvokeAllowedCallerServiceKeys ?? []);
        return spec;
    }

    private static IResult JsonOrNull<T>(T? value) =>
        value is null
            ? Results.Text("null", "application/json")
            : Results.Json(value);

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
