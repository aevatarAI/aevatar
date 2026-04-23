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
        HttpContext http,
        string serviceId,
        ServiceBindingHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        if (ServiceIdentityEndpointAccess.TryResolveContext(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                out var ownerContext,
                out var denied) == false)
        {
            return denied;
        }

        var authenticatedContext = identityResolver.Resolve();
        var bindingKind = ParseBindingKind(request.BindingKind);
        if (TryValidateBoundServiceIdentity(bindingKind, request, authenticatedContext) is { } invalid)
            return invalid;

        var receipt = await commandPort.CreateBindingAsync(new CreateServiceBindingCommand
        {
            Spec = ToSpec(serviceId, request, request.BindingId ?? string.Empty, bindingKind, ownerContext, authenticatedContext),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/bindings/{request.BindingId}", receipt);
    }

    private static async Task<IResult> HandleUpdateAsync(
        HttpContext http,
        string serviceId,
        string bindingId,
        ServiceBindingHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        CancellationToken ct)
    {
        if (ServiceIdentityEndpointAccess.TryResolveContext(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                out var ownerContext,
                out var denied) == false)
        {
            return denied;
        }

        var authenticatedContext = identityResolver.Resolve();
        var bindingKind = ParseBindingKind(request.BindingKind);
        if (TryValidateBoundServiceIdentity(bindingKind, request, authenticatedContext) is { } invalid)
            return invalid;

        var receipt = await commandPort.UpdateBindingAsync(new UpdateServiceBindingCommand
        {
            Spec = ToSpec(serviceId, request, bindingId, bindingKind, ownerContext, authenticatedContext),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/bindings/{bindingId}", receipt);
    }

    private static async Task<IResult> HandleRetireAsync(
        HttpContext http,
        string serviceId,
        string bindingId,
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

        var receipt = await commandPort.RetireBindingAsync(new RetireServiceBindingCommand
        {
            Identity = identity,
            BindingId = bindingId,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/bindings/{bindingId}", receipt);
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

        return JsonOrNull(await queryPort.GetBindingsAsync(identity, ct));
    }

    private static ServiceBindingSpec ToSpec(
        string serviceId,
        ServiceBindingHttpRequest request,
        string bindingId,
        ServiceBindingKind bindingKind,
        ServiceIdentityContext ownerContext,
        ServiceIdentityContext? authenticatedContext)
    {
        var spec = new ServiceBindingSpec
        {
            Identity = GAgentServiceGovernanceEndpointModels.ToIdentity(ownerContext.TenantId, ownerContext.AppId, ownerContext.Namespace, serviceId),
            BindingId = bindingId,
            DisplayName = request.DisplayName ?? string.Empty,
            BindingKind = bindingKind,
        };
        spec.PolicyIds.Add(request.PolicyIds ?? []);
        switch (spec.BindingKind)
        {
            case ServiceBindingKind.Service:
                var boundServiceContext = authenticatedContext ?? new ServiceIdentityContext(
                    request.Service?.TenantId?.Trim() ?? request.TenantId?.Trim() ?? string.Empty,
                    request.Service?.AppId?.Trim() ?? request.AppId?.Trim() ?? string.Empty,
                    request.Service?.Namespace?.Trim() ?? request.Namespace?.Trim() ?? string.Empty,
                    "request");
                spec.ServiceRef = new BoundServiceRef
                {
                    Identity = GAgentServiceGovernanceEndpointModels.ToIdentity(
                        boundServiceContext.TenantId,
                        boundServiceContext.AppId,
                        boundServiceContext.Namespace,
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

    private static IResult? TryValidateBoundServiceIdentity(
        ServiceBindingKind bindingKind,
        ServiceBindingHttpRequest request,
        ServiceIdentityContext? authenticatedContext)
    {
        if (bindingKind != ServiceBindingKind.Service ||
            authenticatedContext is null ||
            request.Service is null)
        {
            return null;
        }

        if (!MatchesAuthenticatedValue(request.Service.TenantId, authenticatedContext.TenantId) ||
            !MatchesAuthenticatedValue(request.Service.AppId, authenticatedContext.AppId) ||
            !MatchesAuthenticatedValue(request.Service.Namespace, authenticatedContext.Namespace))
        {
            return Results.BadRequest(new
            {
                code = "BOUND_SERVICE_IDENTITY_CONFLICT",
                message = "Authenticated service identity does not allow overriding service tenantId, appId, or namespace.",
            });
        }

        return null;
    }

    private static bool MatchesAuthenticatedValue(string? requestedValue, string expectedValue) =>
        string.IsNullOrWhiteSpace(requestedValue) ||
        string.Equals(requestedValue.Trim(), expectedValue, StringComparison.Ordinal);

    private static IResult JsonOrNull<T>(T? value) =>
        value is null
            ? Results.Text("null", "application/json")
            : Results.Json(value);

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
