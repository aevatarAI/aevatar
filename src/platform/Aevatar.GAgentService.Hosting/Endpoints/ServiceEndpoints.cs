using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Hosting.Endpoints;
using Aevatar.GAgentService.Governance.Hosting.Identity;
using Aevatar.GAgentService.Hosting.Serialization;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static partial class ServiceEndpoints
{
    public static IEndpointRouteBuilder MapGAgentServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/services");
        group.MapPost(string.Empty, HandleCreateServiceAsync);
        group.MapPost("/{serviceId}/revisions", HandleCreateRevisionAsync);
        group.MapPost("/{serviceId}/revisions/{revisionId}:prepare", HandlePrepareRevisionAsync);
        group.MapPost("/{serviceId}/revisions/{revisionId}:publish", HandlePublishRevisionAsync);
        group.MapPost("/{serviceId}/revisions/{revisionId}:retire", HandleRetireRevisionAsync);
        group.MapPost("/{serviceId}:default-serving", HandleSetDefaultServingRevisionAsync);
        group.MapPost("/{serviceId}:activate", HandleActivateAsync);
        group.MapGet(string.Empty, HandleListServicesAsync);
        group.MapGet("/{serviceId}", HandleGetServiceAsync);
        group.MapGet("/{serviceId}/revisions", HandleGetRevisionsAsync);
        group.MapPost("/{serviceId}/invoke/{endpointId}", HandleInvokeAsync);
        group.MapGAgentServiceServingEndpoints();
        group.MapGAgentServiceGovernanceEndpoints();
        app.MapScopeServiceEndpoints();
        app.MapScopeWorkflowCapabilityEndpoints();
        app.MapScopeScriptCapabilityEndpoints();
        app.MapScopeGAgentCapabilityEndpoints();
        return app;
    }

    private static async Task<IResult> HandleCreateServiceAsync(
        HttpContext http,
        CreateServiceHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                request.TenantId,
                request.AppId,
                request.Namespace,
                request.ServiceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var receipt = await commandPort.CreateServiceAsync(new CreateServiceDefinitionCommand
        {
            Spec = new ServiceDefinitionSpec
            {
                Identity = identity,
                DisplayName = request.DisplayName ?? string.Empty,
                Endpoints = { request.Endpoints.Select(ToEndpointSpec) },
                PolicyIds = { request.PolicyIds ?? [] },
            },
        }, ct);
        return Results.Accepted($"/api/services/{identity.ServiceId}", receipt);
    }

    private static async Task<IResult> HandleCreateRevisionAsync(
        HttpContext http,
        string serviceId,
        CreateRevisionHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
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

        var spec = new ServiceRevisionSpec
        {
            Identity = identity,
            RevisionId = request.RevisionId ?? string.Empty,
            ImplementationKind = ParseImplementationKind(request.ImplementationKind),
        };

        switch (spec.ImplementationKind)
        {
            case ServiceImplementationKind.Static:
                spec.StaticSpec = new StaticServiceRevisionSpec
                {
                    ActorTypeName = request.Static?.ActorTypeName ?? string.Empty,
                    PreferredActorId = request.Static?.PreferredActorId ?? string.Empty,
                    Endpoints = { (request.Static?.Endpoints ?? []).Select(ToEndpointDescriptor) },
                };
                break;
            case ServiceImplementationKind.Scripting:
                spec.ScriptingSpec = new ScriptingServiceRevisionSpec
                {
                    ScriptId = request.Scripting?.ScriptId ?? string.Empty,
                    Revision = request.Scripting?.Revision ?? string.Empty,
                    DefinitionActorId = request.Scripting?.DefinitionActorId ?? string.Empty,
                };
                break;
            case ServiceImplementationKind.Workflow:
                spec.WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowName = request.Workflow?.WorkflowName ?? string.Empty,
                    WorkflowYaml = request.Workflow?.WorkflowYaml ?? string.Empty,
                    DefinitionActorId = request.Workflow?.DefinitionActorId ?? string.Empty,
                };
                if (request.Workflow?.InlineWorkflowYamls != null)
                {
                    foreach (var entry in request.Workflow.InlineWorkflowYamls)
                    {
                        spec.WorkflowSpec.InlineWorkflowYamls.Add(entry.Key, entry.Value);
                    }
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported implementation kind '{request.ImplementationKind}'.");
        }

        var receipt = await commandPort.CreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = spec,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/revisions/{request.RevisionId}", receipt);
    }

    private static async Task<IResult> HandlePrepareRevisionAsync(
        HttpContext http,
        string serviceId,
        string revisionId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
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

        var receipt = await commandPort.PrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity,
            RevisionId = revisionId,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/revisions/{revisionId}", receipt);
    }

    private static async Task<IResult> HandlePublishRevisionAsync(
        HttpContext http,
        string serviceId,
        string revisionId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
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

        var receipt = await commandPort.PublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity,
            RevisionId = revisionId,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/revisions/{revisionId}", receipt);
    }

    private static async Task<IResult> HandleRetireRevisionAsync(
        HttpContext http,
        string serviceId,
        string revisionId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
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

        var receipt = await commandPort.RetireRevisionAsync(new RetireServiceRevisionCommand
        {
            Identity = identity,
            RevisionId = revisionId,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/revisions/{revisionId}", receipt);
    }

    private static async Task<IResult> HandleSetDefaultServingRevisionAsync(
        HttpContext http,
        string serviceId,
        SetDefaultServingRevisionHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
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

        var receipt = await commandPort.SetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity,
            RevisionId = request.RevisionId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}", receipt);
    }

    private static async Task<IResult> HandleActivateAsync(
        HttpContext http,
        string serviceId,
        ActivateServiceRevisionHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
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

        var receipt = await commandPort.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity,
            RevisionId = request.RevisionId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}", receipt);
    }

    private static async Task<IResult> HandleListServicesAsync(
        HttpContext http,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceLifecycleQueryPort queryPort,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveContext(
                identityResolver,
                query.TenantId,
                query.AppId,
                query.Namespace,
                out var context,
                out var denied))
        {
            return denied;
        }

        var services = await queryPort.ListServicesAsync(context.TenantId, context.AppId, context.Namespace, query.Take, ct);
        return JsonOrNull(services);
    }

    private static async Task<IResult> HandleGetServiceAsync(
        HttpContext http,
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceLifecycleQueryPort queryPort,
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

        return JsonOrNull(await queryPort.GetServiceAsync(identity, ct));
    }

    private static async Task<IResult> HandleGetRevisionsAsync(
        HttpContext http,
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceLifecycleQueryPort queryPort,
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

        return JsonOrNull(await queryPort.GetServiceRevisionsAsync(identity, ct));
    }

    private static async Task<IResult> HandleInvokeAsync(
        HttpContext http,
        string serviceId,
        string endpointId,
        InvokeServiceHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceInvocationPort invocationPort,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionArtifactStore artifactStore,
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

        Any payload;
        string revisionId;
        try
        {
            (payload, revisionId) = await ResolveInvocationPayloadAsync(
                request,
                identity,
                catalogReader,
                artifactStore,
                ct);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SERVICE_INVOKE_REQUEST",
                message = ex.Message,
            });
        }

        var receipt = await invocationPort.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = identity,
            EndpointId = endpointId,
            CommandId = request.CommandId ?? string.Empty,
            CorrelationId = request.CorrelationId ?? string.Empty,
            RevisionId = revisionId,
            Payload = payload,
            Caller = ResolveInvocationCaller(identityResolver, request),
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}", receipt);
    }

    private static async Task<(Any Payload, string RevisionId)> ResolveInvocationPayloadAsync(
        InvokeServiceHttpRequest request,
        ServiceIdentity identity,
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionArtifactStore artifactStore,
        CancellationToken ct)
    {
        var typeUrl = request.PayloadTypeUrl ?? string.Empty;
        var requestedRevisionId = request.RevisionId?.Trim() ?? string.Empty;
        var hasJson = !string.IsNullOrWhiteSpace(request.PayloadJson);
        var hasBase64 = !string.IsNullOrWhiteSpace(request.PayloadBase64);
        if (hasJson && hasBase64)
            throw new InvalidOperationException(
                "payloadJson and payloadBase64 are mutually exclusive; specify only one.");

        if (hasJson)
        {
            if (string.IsNullOrWhiteSpace(typeUrl))
                throw new InvalidOperationException("payloadTypeUrl is required when payloadJson is provided.");

            var revisionId = requestedRevisionId;
            if (string.IsNullOrWhiteSpace(revisionId))
            {
                var catalog = await catalogReader.GetAsync(identity, ct);
                revisionId = catalog?.ActiveServingRevisionId ?? string.Empty;
            }

            var packed = await ServiceJsonPayloads.PackJsonAsync(
                artifactStore,
                ServiceKeys.Build(identity),
                revisionId,
                typeUrl,
                request.PayloadJson!,
                ct);
            return (packed, revisionId);
        }

        return (ServiceJsonPayloads.PackBase64(typeUrl, request.PayloadBase64), requestedRevisionId);
    }

    private static ServiceInvocationCaller ResolveInvocationCaller(
        IServiceIdentityContextResolver identityResolver,
        InvokeServiceHttpRequest request)
    {
        var authenticatedContext = identityResolver.Resolve();
        if (authenticatedContext is null)
        {
            return new ServiceInvocationCaller
            {
                ServiceKey = request.CallerServiceKey?.Trim() ?? string.Empty,
                TenantId = request.CallerTenantId?.Trim() ?? string.Empty,
                AppId = request.CallerAppId?.Trim() ?? string.Empty,
            };
        }

        return new ServiceInvocationCaller
        {
            // Authenticated /api/services callers do not currently carry a
            // verifiable caller service id/service key contract.
            ServiceKey = string.Empty,
            TenantId = authenticatedContext.TenantId,
            AppId = authenticatedContext.AppId,
        };
    }

    private static IResult JsonOrNull<T>(T? value) =>
        value is null
            ? Results.Text("null", "application/json")
            : Results.Json(value);

    internal static ServiceIdentity ToIdentity(string? tenantId, string? appId, string? @namespace, string serviceId)
    {
        return new ServiceIdentity
        {
            TenantId = tenantId?.Trim() ?? string.Empty,
            AppId = appId?.Trim() ?? string.Empty,
            Namespace = @namespace?.Trim() ?? string.Empty,
            ServiceId = serviceId?.Trim() ?? string.Empty,
        };
    }

    private static ServiceEndpointSpec ToEndpointSpec(ServiceEndpointHttpRequest request) =>
        new()
        {
            EndpointId = request.EndpointId ?? string.Empty,
            DisplayName = request.DisplayName ?? string.Empty,
            Kind = ParseEndpointKind(request.Kind),
            RequestTypeUrl = request.RequestTypeUrl ?? string.Empty,
            ResponseTypeUrl = request.ResponseTypeUrl ?? string.Empty,
            Description = request.Description ?? string.Empty,
        };

    private static ServiceEndpointDescriptor ToEndpointDescriptor(ServiceEndpointHttpRequest request) =>
        new()
        {
            EndpointId = request.EndpointId ?? string.Empty,
            DisplayName = request.DisplayName ?? string.Empty,
            Kind = ParseEndpointKind(request.Kind),
            RequestTypeUrl = request.RequestTypeUrl ?? string.Empty,
            ResponseTypeUrl = request.ResponseTypeUrl ?? string.Empty,
            Description = request.Description ?? string.Empty,
        };

    private static ServiceImplementationKind ParseImplementationKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "static" => ServiceImplementationKind.Static,
            "scripting" => ServiceImplementationKind.Scripting,
            "workflow" => ServiceImplementationKind.Workflow,
            _ => throw new InvalidOperationException($"Unsupported implementation kind '{rawValue}'."),
        };
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

    public sealed record ServiceIdentityQuery(
        string? TenantId,
        string? AppId,
        string? Namespace,
        int Take = 200);

    public sealed record ServiceIdentityHttpRequest(
        string TenantId,
        string AppId,
        string Namespace);

    public sealed record ServiceEndpointHttpRequest(
        string EndpointId,
        string DisplayName,
        string Kind,
        string RequestTypeUrl,
        string ResponseTypeUrl,
        string Description);

    public sealed record CreateServiceHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        string ServiceId,
        string DisplayName,
        IReadOnlyList<ServiceEndpointHttpRequest> Endpoints,
        IReadOnlyList<string>? PolicyIds = null);

    public sealed record StaticRevisionHttpRequest(
        string ActorTypeName,
        string? PreferredActorId,
        IReadOnlyList<ServiceEndpointHttpRequest> Endpoints);

    public sealed record ScriptingRevisionHttpRequest(
        string ScriptId,
        string Revision,
        string DefinitionActorId);

    public sealed record WorkflowRevisionHttpRequest(
        string WorkflowName,
        string WorkflowYaml,
        string? DefinitionActorId,
        IReadOnlyDictionary<string, string>? InlineWorkflowYamls);

    public sealed record CreateRevisionHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        string RevisionId,
        string ImplementationKind,
        StaticRevisionHttpRequest? Static,
        ScriptingRevisionHttpRequest? Scripting,
        WorkflowRevisionHttpRequest? Workflow);

    public sealed record SetDefaultServingRevisionHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        string RevisionId);

    public sealed record ActivateServiceRevisionHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        string RevisionId);

    public sealed record InvokeServiceHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        string? CommandId,
        string? CorrelationId,
        string? PayloadTypeUrl,
        string? PayloadBase64,
        string? CallerServiceKey = null,
        string? CallerTenantId = null,
        string? CallerAppId = null,
        string? PayloadJson = null,
        string? RevisionId = null);
}
