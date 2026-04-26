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

internal static class ServiceEndpointCatalogEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{serviceId}/endpoint-catalog", HandleCreateAsync);
        group.MapPut("/{serviceId}/endpoint-catalog", HandleUpdateAsync);
        group.MapGet("/{serviceId}/endpoint-catalog", HandleGetAsync);
    }

    private static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        string serviceId,
        ServiceEndpointCatalogHttpRequest request,
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

        var receipt = await commandPort.CreateEndpointCatalogAsync(new CreateServiceEndpointCatalogCommand
        {
            Spec = ToSpec(serviceId, request, identityResolver),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/endpoint-catalog", receipt);
    }

    private static async Task<IResult> HandleUpdateAsync(
        HttpContext http,
        string serviceId,
        ServiceEndpointCatalogHttpRequest request,
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

        var receipt = await commandPort.UpdateEndpointCatalogAsync(new UpdateServiceEndpointCatalogCommand
        {
            Spec = ToSpec(serviceId, request, identityResolver),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/endpoint-catalog", receipt);
    }

    private static async Task<IResult> HandleGetAsync(
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

        return JsonOrNull(await queryPort.GetEndpointCatalogAsync(identity, ct));
    }

    private static ServiceEndpointCatalogSpec ToSpec(
        string serviceId,
        ServiceEndpointCatalogHttpRequest request,
        IServiceIdentityContextResolver identityResolver)
    {
        var context = identityResolver.Resolve() ?? new ServiceIdentityContext(
            request.TenantId?.Trim() ?? string.Empty,
            request.AppId?.Trim() ?? string.Empty,
            request.Namespace?.Trim() ?? string.Empty,
            "request");
        var spec = new ServiceEndpointCatalogSpec
        {
            Identity = GAgentServiceGovernanceEndpointModels.ToIdentity(context.TenantId, context.AppId, context.Namespace, serviceId),
        };
        spec.Endpoints.Add((request.Endpoints ?? [])
            .Select(x => new ServiceEndpointExposureSpec
            {
                EndpointId = x.EndpointId ?? string.Empty,
                DisplayName = x.DisplayName ?? string.Empty,
                Kind = ParseEndpointKind(x.Kind),
                RequestTypeUrl = x.RequestTypeUrl ?? string.Empty,
                ResponseTypeUrl = x.ResponseTypeUrl ?? string.Empty,
                Description = x.Description ?? string.Empty,
                ExposureKind = ParseExposureKind(x.ExposureKind),
                PolicyIds = { x.PolicyIds ?? [] },
            }));
        return spec;
    }

    private static IResult JsonOrNull<T>(T? value) =>
        value is null
            ? Results.Text("null", "application/json")
            : Results.Json(value);

    private static ServiceEndpointKind ParseEndpointKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "command" => ServiceEndpointKind.Command,
            "chat" => ServiceEndpointKind.Chat,
            _ => ServiceEndpointKind.Command,
        };
    }

    private static ServiceEndpointExposureKind ParseExposureKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "internal" => ServiceEndpointExposureKind.Internal,
            "public" => ServiceEndpointExposureKind.Public,
            "disabled" => ServiceEndpointExposureKind.Disabled,
            _ => ServiceEndpointExposureKind.Internal,
        };
    }

    public sealed record ServiceEndpointExposureHttpRequest(
        string EndpointId,
        string DisplayName,
        string Kind,
        string RequestTypeUrl,
        string ResponseTypeUrl,
        string Description,
        string ExposureKind,
        IReadOnlyList<string>? PolicyIds = null);

    public sealed record ServiceEndpointCatalogHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        IReadOnlyList<ServiceEndpointExposureHttpRequest> Endpoints);
}
