using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.GAgentService.Governance.Hosting.Endpoints;

internal static class ServiceBindingEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/{serviceId}/bindings", HandleCreateAsync);
        group.MapPut("/{serviceId}/bindings/{bindingId}", HandleUpdateAsync);
        group.MapPost("/{serviceId}/bindings/{bindingId}:retire", HandleRetireAsync);
        group.MapGet("/{serviceId}/bindings", HandleGetAsync);
    }

    private static async Task<IResult> HandleCreateAsync(
        string serviceId,
        ServiceBindingHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.CreateBindingAsync(new CreateServiceBindingCommand
        {
            Spec = ToSpec(serviceId, request, request.BindingId ?? string.Empty),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/bindings/{request.BindingId}", receipt);
    }

    private static async Task<IResult> HandleUpdateAsync(
        string serviceId,
        string bindingId,
        ServiceBindingHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.UpdateBindingAsync(new UpdateServiceBindingCommand
        {
            Spec = ToSpec(serviceId, request, bindingId),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/bindings/{bindingId}", receipt);
    }

    private static async Task<IResult> HandleRetireAsync(
        string serviceId,
        string bindingId,
        GAgentServiceGovernanceEndpointModels.ServiceIdentityHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.RetireBindingAsync(new RetireServiceBindingCommand
        {
            Identity = GAgentServiceGovernanceEndpointModels.ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            BindingId = bindingId,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/bindings/{bindingId}", receipt);
    }

    private static Task<ServiceBindingCatalogSnapshot?> HandleGetAsync(
        string serviceId,
        [AsParameters] GAgentServiceGovernanceEndpointModels.ServiceIdentityQuery query,
        [FromServices] IServiceGovernanceQueryPort queryPort,
        CancellationToken ct) =>
        queryPort.GetBindingsAsync(
            GAgentServiceGovernanceEndpointModels.ToIdentity(query.TenantId, query.AppId, query.Namespace, serviceId),
            ct);

    private static ServiceBindingSpec ToSpec(string serviceId, ServiceBindingHttpRequest request, string bindingId)
    {
        var spec = new ServiceBindingSpec
        {
            Identity = GAgentServiceGovernanceEndpointModels.ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            BindingId = bindingId,
            DisplayName = request.DisplayName ?? string.Empty,
            BindingKind = ParseBindingKind(request.BindingKind),
        };
        spec.PolicyIds.Add(request.PolicyIds ?? []);
        switch (spec.BindingKind)
        {
            case ServiceBindingKind.Service:
                spec.ServiceRef = new BoundServiceRef
                {
                    Identity = GAgentServiceGovernanceEndpointModels.ToIdentity(
                        request.Service?.TenantId ?? request.TenantId,
                        request.Service?.AppId ?? request.AppId,
                        request.Service?.Namespace ?? request.Namespace,
                        request.Service?.ServiceId ?? string.Empty),
                    EndpointId = request.Service?.EndpointId ?? string.Empty,
                };
                break;
            case ServiceBindingKind.Connector:
                spec.ConnectorRef = new BoundConnectorRef
                {
                    ConnectorType = request.Connector?.ConnectorType ?? string.Empty,
                    ConnectorId = request.Connector?.ConnectorId ?? string.Empty,
                };
                break;
            case ServiceBindingKind.Secret:
                spec.SecretRef = new BoundSecretRef
                {
                    SecretName = request.Secret?.SecretName ?? string.Empty,
                };
                break;
            default:
                throw new InvalidOperationException($"Unsupported binding kind '{request.BindingKind}'.");
        }

        return spec;
    }

    private static ServiceBindingKind ParseBindingKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "service" => ServiceBindingKind.Service,
            "connector" => ServiceBindingKind.Connector,
            "secret" => ServiceBindingKind.Secret,
            _ => throw new InvalidOperationException($"Unsupported binding kind '{rawValue}'."),
        };
    }

    public sealed record BoundServiceHttpRequest(
        string ServiceId,
        string? EndpointId,
        string? TenantId = null,
        string? AppId = null,
        string? Namespace = null);

    public sealed record BoundConnectorHttpRequest(
        string ConnectorType,
        string ConnectorId);

    public sealed record BoundSecretHttpRequest(
        string SecretName);

    public sealed record ServiceBindingHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        string? BindingId,
        string DisplayName,
        string BindingKind,
        BoundServiceHttpRequest? Service,
        BoundConnectorHttpRequest? Connector,
        BoundSecretHttpRequest? Secret,
        IReadOnlyList<string>? PolicyIds = null);
}
