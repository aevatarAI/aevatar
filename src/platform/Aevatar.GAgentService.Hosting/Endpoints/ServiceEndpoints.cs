using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Hosting.Endpoints;
using Aevatar.GAgentService.Hosting.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ServiceEndpoints
{
    public static IEndpointRouteBuilder MapGAgentServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/services");
        group.MapPost(string.Empty, HandleCreateServiceAsync);
        group.MapPost("/{serviceId}/revisions", HandleCreateRevisionAsync);
        group.MapPost("/{serviceId}/revisions/{revisionId}:prepare", HandlePrepareRevisionAsync);
        group.MapPost("/{serviceId}/revisions/{revisionId}:publish", HandlePublishRevisionAsync);
        group.MapPost("/{serviceId}:default-serving", HandleSetDefaultServingRevisionAsync);
        group.MapPost("/{serviceId}:activate", HandleActivateAsync);
        group.MapGet(string.Empty, HandleListServicesAsync);
        group.MapGet("/{serviceId}", HandleGetServiceAsync);
        group.MapGet("/{serviceId}/revisions", HandleGetRevisionsAsync);
        group.MapPost("/{serviceId}/invoke/{endpointId}", HandleInvokeAsync);
        group.MapGAgentServiceGovernanceEndpoints();
        return app;
    }

    private static async Task<IResult> HandleCreateServiceAsync(
        CreateServiceHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.CreateServiceAsync(new CreateServiceDefinitionCommand
        {
            Spec = new ServiceDefinitionSpec
            {
                Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, request.ServiceId),
                DisplayName = request.DisplayName ?? string.Empty,
                Endpoints = { request.Endpoints.Select(ToEndpointSpec) },
                PolicyIds = { request.PolicyIds ?? [] },
            },
        }, ct);
        return Results.Accepted($"/api/services/{request.ServiceId}", receipt);
    }

    private static async Task<IResult> HandleCreateRevisionAsync(
        string serviceId,
        CreateRevisionHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var spec = new ServiceRevisionSpec
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
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
        string serviceId,
        string revisionId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.PrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            RevisionId = revisionId,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/revisions/{revisionId}", receipt);
    }

    private static async Task<IResult> HandlePublishRevisionAsync(
        string serviceId,
        string revisionId,
        ServiceIdentityHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.PublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            RevisionId = revisionId,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}/revisions/{revisionId}", receipt);
    }

    private static async Task<IResult> HandleSetDefaultServingRevisionAsync(
        string serviceId,
        SetDefaultServingRevisionHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.SetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            RevisionId = request.RevisionId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}", receipt);
    }

    private static async Task<IResult> HandleActivateAsync(
        string serviceId,
        ActivateServiceHttpRequest request,
        [FromServices] IServiceCommandPort commandPort,
        CancellationToken ct)
    {
        var receipt = await commandPort.ActivateServingRevisionAsync(new ActivateServingRevisionCommand
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            RevisionId = request.RevisionId ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}", receipt);
    }

    private static Task<IReadOnlyList<ServiceCatalogSnapshot>> HandleListServicesAsync(
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceQueryPort queryPort,
        CancellationToken ct) =>
        queryPort.ListServicesAsync(query.TenantId ?? string.Empty, query.AppId ?? string.Empty, query.Namespace ?? string.Empty, query.Take, ct);

    private static Task<ServiceCatalogSnapshot?> HandleGetServiceAsync(
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceQueryPort queryPort,
        CancellationToken ct) =>
        queryPort.GetServiceAsync(
            ToIdentity(query.TenantId, query.AppId, query.Namespace, serviceId),
            ct);

    private static Task<ServiceRevisionCatalogSnapshot?> HandleGetRevisionsAsync(
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceQueryPort queryPort,
        CancellationToken ct) =>
        queryPort.GetServiceRevisionsAsync(
            ToIdentity(query.TenantId, query.AppId, query.Namespace, serviceId),
            ct);

    private static async Task<IResult> HandleInvokeAsync(
        string serviceId,
        string endpointId,
        InvokeServiceHttpRequest request,
        [FromServices] IServiceInvocationPort invocationPort,
        CancellationToken ct)
    {
        var receipt = await invocationPort.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = ToIdentity(request.TenantId, request.AppId, request.Namespace, serviceId),
            EndpointId = endpointId,
            CommandId = request.CommandId ?? string.Empty,
            CorrelationId = request.CorrelationId ?? string.Empty,
            Payload = ServiceJsonPayloads.PackBase64(
                request.PayloadTypeUrl ?? string.Empty,
                request.PayloadBase64),
            Caller = new ServiceInvocationCaller
            {
                ServiceKey = request.CallerServiceKey ?? string.Empty,
                TenantId = request.CallerTenantId ?? string.Empty,
                AppId = request.CallerAppId ?? string.Empty,
            },
        }, ct);
        return Results.Accepted($"/api/services/{serviceId}", receipt);
    }

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

    public sealed record ActivateServiceHttpRequest(
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
        string? CallerAppId = null);
}
