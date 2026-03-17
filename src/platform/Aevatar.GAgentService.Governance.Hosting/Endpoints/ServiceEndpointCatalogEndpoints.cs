using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
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
        string serviceId,
        ServiceEndpointCatalogHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.CreateEndpointCatalogAsync(new CreateServiceEndpointCatalogCommand
        {
            Spec = ToSpec(serviceId, request),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/endpoint-catalog", receipt);
    }

    private static async Task<IResult> HandleUpdateAsync(
        string serviceId,
        ServiceEndpointCatalogHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.UpdateEndpointCatalogAsync(new UpdateServiceEndpointCatalogCommand
        {
            Spec = ToSpec(serviceId, request),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/endpoint-catalog", receipt);
    }

    private static Task<ServiceEndpointCatalogSnapshot?> HandleGetAsync(
        string serviceId,
        [AsParameters] GAgentServiceGovernanceEndpointModels.ServiceIdentityQuery query,
        [FromServices] IServiceGovernanceQueryPort queryPort,
        CancellationToken ct) =>
        queryPort.GetEndpointCatalogAsync(
            GAgentServiceGovernanceEndpointModels.ToIdentity(query.TenantId, query.AppId, query.Namespace, serviceId),
            ct);

    private static ServiceEndpointCatalogSpec ToSpec(string serviceId, ServiceEndpointCatalogHttpRequest request)
    {
        var spec = new ServiceEndpointCatalogSpec
        {
            Identity = GAgentServiceGovernanceEndpointModels.ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
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
