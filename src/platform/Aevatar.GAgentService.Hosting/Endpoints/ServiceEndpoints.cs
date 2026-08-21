using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Governance.Hosting.Endpoints;
using Aevatar.GAgentService.Governance.Hosting.Identity;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using Aevatar.GAgentService.Hosting.Serialization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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
        group.MapPost("/{serviceId}:activate", HandleActivateAsync);
        group.MapGet(string.Empty, HandleListServicesAsync);
        group.MapGet("/{serviceId}", HandleGetServiceAsync);
        group.MapGet("/{serviceId}/revisions", HandleGetRevisionsAsync);
        group.MapGAgentServiceOpenApiEndpoints();
        group.MapPost("/{serviceId}/invoke/{endpointId}", HandleInvokeAsync);
        group.MapGAgentServiceServingEndpoints();
        group.MapGAgentServiceGovernanceEndpoints();
        app.MapScopeServiceEndpoints();
        app.MapScopeWorkflowCapabilityEndpoints();
        // Scope script endpoints exist only when the host composed the scripting capability;
        // without it the routes are absent entirely (404) instead of resolving to missing services.
        if (app.ServiceProvider.GetService<IScopeScriptQueryPort>() is not null)
            app.MapScopeScriptCapabilityEndpoints();
        app.MapScopeGAgentCapabilityEndpoints();
        app.MapScheduledDispatchEndpoints();
        return app;
    }

    public static IEndpointRouteBuilder MapScheduledDispatchEndpoints(this IEndpointRouteBuilder app)
    {
        if (HasRoute(app, "/api/schedules"))
            return app;

        ScheduledDispatchEndpoints.Map(app.MapGroup("/api"));
        return app;
    }

    private static bool HasRoute(IEndpointRouteBuilder app, string routePattern) =>
        app.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Any(x => string.Equals(x.RoutePattern.RawText, routePattern, StringComparison.Ordinal));

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

        var spec = new ServiceDefinitionSpec
        {
            Identity = identity,
            DisplayName = request.DisplayName ?? string.Empty,
            Endpoints = { request.Endpoints.Select(ToEndpointSpec) },
            PolicyIds = { request.PolicyIds ?? [] },
        };

        var receipt = await commandPort.CreateServiceAsync(new CreateServiceDefinitionCommand
        {
            Spec = spec,
        }, ct);
        return Results.Accepted($"/api/services/{identity.ServiceId}", receipt);
    }

    private static async Task<IResult> HandleCreateRevisionAsync(
        HttpContext http,
        string serviceId,
        CreateRevisionHttpRequest request,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCommandPort commandPort,
        [FromServices] IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService,
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
                    AgentKind = request.Static?.AgentKind ?? string.Empty,
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
                var workflowRequest = request.Workflow;
                spec.WorkflowSpec = new WorkflowServiceRevisionSpec
                {
                    WorkflowId = workflowRequest?.WorkflowId ?? string.Empty,
                    WorkflowName = workflowRequest?.WorkflowName ?? string.Empty,
                    WorkflowYaml = workflowRequest?.WorkflowYaml ?? string.Empty,
                    DefinitionActorId = workflowRequest?.DefinitionActorId ?? string.Empty,
                    ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                };
                if (workflowRequest?.InlineWorkflowYamls != null)
                {
                    foreach (var entry in workflowRequest.InlineWorkflowYamls)
                    {
                        spec.WorkflowSpec.InlineWorkflowYamls.Add(entry.Key, entry.Value);
                    }
                }

                try
                {
                    var admissionContext = await WorkflowCapabilityAdmissionHttpContext.CreateAsync(
                        http,
                        ExternalCapabilityExecutionMode.Durable,
                        explicitRequestConfirmations: request.ExplicitRequestConfirmations,
                        ct: ct);
                    spec.WorkflowSpec.ExpectedExecutionMode = admissionContext.ExecutionMode;
                    spec.WorkflowSpec.CapabilityAdmissionPlan = await capabilityAdmissionService.AdmitAsync(
                        new WorkflowExternalCapabilityAdmissionRequest(
                            new ExternalWorkflowCapabilityAccessContext(
                                identity.TenantId,
                                admissionContext.CallerId,
                                admissionContext.NyxIdCallerCredential,
                                admissionContext.NyxIdOrganizationBearerToken),
                            spec.WorkflowSpec.WorkflowYaml,
                            spec.WorkflowSpec.InlineWorkflowYamls,
                            "service_revision",
                            admissionContext.ExecutionMode,
                            admissionContext.ExplicitRequestConfirmations,
                            workflowRequest?.WorkflowId,
                            request.RevisionId),
                        ct);
                }
                catch (WorkflowCallerCredentialSelectionException)
                {
                    return Results.BadRequest(new
                    {
                        code = WorkflowCallerCredentialSelectionException.ErrorCode,
                        message = WorkflowCallerCredentialSelectionException.SafeMessage,
                    });
                }
                catch (WorkflowExternalCapabilityAdmissionException ex)
                {
                    return Results.BadRequest(new
                    {
                        code = "WORKFLOW_EXTERNAL_CAPABILITY_NOT_READY",
                        message = "External workflow capability admission failed.",
                        readiness = new
                        {
                            status = ex.Readiness.Status.ToString(),
                            blockers = ex.Readiness.Blockers.Select(static blocker => new
                            {
                                code = blocker.Code,
                                safeMessage = blocker.SafeMessage,
                            }),
                        },
                    });
                }
                catch (NyxIdExplicitRequestConfirmationInputException ex)
                {
                    return Results.BadRequest(new
                    {
                        code = NyxIdExplicitRequestConfirmationInputException.ErrorCode,
                        message = ex.Message,
                    });
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
        [FromServices] IServiceInvocationCatalogQueryReader invocationCatalogQueryReader,
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
        return Results.Json(await JoinInvokeReadinessAsync(services, invocationCatalogQueryReader, ct));
    }

    private static async Task<IResult> HandleGetServiceAsync(
        HttpContext http,
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceLifecycleQueryPort queryPort,
        [FromServices] IServiceInvocationCatalogQueryReader invocationCatalogQueryReader,
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

        var service = await queryPort.GetServiceAsync(identity, ct);
        if (service == null)
            return JsonOrNull<ServiceWithInvokeReadinessHttpResponse>(null);

        return Results.Json(await JoinInvokeReadinessAsync(service, invocationCatalogQueryReader, ct));
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
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        [FromServices] ServiceInvokeReadinessErrorMapper readinessErrorMapper,
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
                revisionCatalogReader,
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

        ServiceInvocationAcceptedReceipt receipt;
        try
        {
            receipt = await invocationPort.InvokeAsync(new ServiceInvocationRequest
            {
                Identity = identity,
                EndpointId = endpointId,
                CommandId = request.CommandId ?? string.Empty,
                CorrelationId = request.CorrelationId ?? string.Empty,
                RevisionId = revisionId,
                Payload = payload,
                Caller = ResolveInvocationCaller(identityResolver, request),
            }, ct);
        }
        catch (ServiceInvokeReadinessException ex)
        {
            return Results.BadRequest(readinessErrorMapper.Map(ex));
        }
        // Refactor (iter56/cluster-891-endpoint-ack-honesty): old=200-shaped accepted, new=202 + Location
        //   Service invoke is accepted for dispatch; the run resource is the status surface for outcome.
        //   Never point Location at the service definition root because that is not the command/run status.
        receipt.StatusUrl = BuildServiceRunStatusUrl(identity, receipt);
        return Results.Accepted(receipt.StatusUrl, receipt);
    }

    private static string BuildServiceRunStatusUrl(ServiceIdentity identity, ServiceInvocationAcceptedReceipt receipt) =>
        $"/api/scopes/{Uri.EscapeDataString(identity.TenantId)}/services/{Uri.EscapeDataString(identity.ServiceId)}/runs/{Uri.EscapeDataString(ResolveAcceptedRunId(receipt))}";

    private static string ResolveAcceptedRunId(ServiceInvocationAcceptedReceipt receipt) =>
        string.IsNullOrWhiteSpace(receipt.RunId) ? receipt.CommandId : receipt.RunId;

    private static async Task<(Any Payload, string RevisionId)> ResolveInvocationPayloadAsync(
        InvokeServiceHttpRequest request,
        ServiceIdentity identity,
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
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
                revisionCatalogReader,
                identity,
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

    private static async Task<IReadOnlyList<ServiceWithInvokeReadinessHttpResponse>> JoinInvokeReadinessAsync(
        IReadOnlyList<ServiceCatalogSnapshot> services,
        IServiceInvocationCatalogQueryReader invocationCatalogQueryReader,
        CancellationToken ct)
    {
        var responses = new List<ServiceWithInvokeReadinessHttpResponse>(services.Count);
        foreach (var service in services)
            responses.Add(await JoinInvokeReadinessAsync(service, invocationCatalogQueryReader, ct));

        return responses;
    }

    private static async Task<ServiceWithInvokeReadinessHttpResponse> JoinInvokeReadinessAsync(
        ServiceCatalogSnapshot service,
        IServiceInvocationCatalogQueryReader invocationCatalogQueryReader,
        CancellationToken ct)
    {
        var catalog = HasCompleteInvocationIdentity(service)
            ? await invocationCatalogQueryReader.GetAsync(ToIdentity(service.TenantId, service.AppId, service.Namespace, service.ServiceId), ct)
            : null;
        var entries = catalog?.Entries ?? [];
        var ready = entries.Count > 0 &&
                    entries.All(x => x.ReadinessStatus == ServiceInvokeReadinessStatus.Ready);
        var status = entries.Count == 0
            ? ServiceInvokeReadinessStatus.Unspecified
            : ready
                ? ServiceInvokeReadinessStatus.Ready
                : ServiceInvokeReadinessStatus.Unavailable;
        var reason = status == ServiceInvokeReadinessStatus.Unavailable
            ? entries.FirstOrDefault(x => x.UnavailableReason != ServiceInvokeUnavailableReason.Unspecified)?.UnavailableReason.ToString()
            : null;

        return new ServiceWithInvokeReadinessHttpResponse(
            service.ServiceKey,
            service.TenantId,
            service.AppId,
            service.Namespace,
            service.ServiceId,
            service.DisplayName,
            service.DefaultServingRevisionId,
            service.ActiveServingRevisionId,
            service.DeploymentId,
            service.PrimaryActorId,
            service.DeploymentStatus,
            service.Endpoints,
            service.PolicyIds,
            service.UpdatedAt,
            ready,
            status.ToString(),
            reason,
            MapExternalExposure(service.ExternalExposure));
    }

    private static bool HasCompleteInvocationIdentity(ServiceCatalogSnapshot service) =>
        !string.IsNullOrWhiteSpace(service.TenantId) &&
        !string.IsNullOrWhiteSpace(service.AppId) &&
        !string.IsNullOrWhiteSpace(service.Namespace) &&
        !string.IsNullOrWhiteSpace(service.ServiceId);

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

    private static ExternalExposureHttpResponse? MapExternalExposure(
        ServiceExternalExposureSnapshot? externalExposure)
    {
        if (externalExposure == null)
            return null;

        if (string.IsNullOrWhiteSpace(externalExposure.NyxidSlug) &&
            externalExposure.RegisteredAt == null &&
            externalExposure.Status == ServiceRegistrationStatus.Unspecified &&
            string.IsNullOrWhiteSpace(externalExposure.NyxidServiceId) &&
            string.IsNullOrWhiteSpace(externalExposure.DesiredSpecHash) &&
            string.IsNullOrWhiteSpace(externalExposure.RegisteredSpecHash) &&
            string.IsNullOrWhiteSpace(externalExposure.LastError) &&
            externalExposure.Attempt == 0 &&
            externalExposure.NextAttemptAt == null &&
            string.IsNullOrWhiteSpace(externalExposure.CredentialKid) &&
            !externalExposure.ExposureDesired)
        {
            return null;
        }

        return new ExternalExposureHttpResponse(
            externalExposure.NyxidSlug ?? string.Empty,
            externalExposure.RegisteredAt,
            externalExposure.Status.ToString(),
            externalExposure.NyxidServiceId ?? string.Empty,
            externalExposure.DesiredSpecHash ?? string.Empty,
            externalExposure.RegisteredSpecHash ?? string.Empty,
            externalExposure.LastError ?? string.Empty,
            externalExposure.Attempt,
            externalExposure.NextAttemptAt,
            externalExposure.CredentialKid ?? string.Empty,
            externalExposure.ExposureDesired,
            externalExposure.SourceStateVersion);
    }

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

    public sealed record ServiceWithInvokeReadinessHttpResponse(
        string ServiceKey,
        string TenantId,
        string AppId,
        string Namespace,
        string ServiceId,
        string DisplayName,
        string DefaultServingRevisionId,
        string ActiveServingRevisionId,
        string DeploymentId,
        string PrimaryActorId,
        string DeploymentStatus,
        IReadOnlyList<ServiceEndpointSnapshot> Endpoints,
        IReadOnlyList<string> PolicyIds,
        DateTimeOffset UpdatedAt,
        bool InvokeReady,
        string InvokeReadinessStatus,
        string? InvokeUnavailableReason,
        ExternalExposureHttpResponse? ExternalExposure = null);

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

    public sealed record ExternalExposureHttpResponse(
        string NyxidSlug,
        DateTimeOffset? RegisteredAt,
        string Status = "",
        string NyxidServiceId = "",
        string DesiredSpecHash = "",
        string RegisteredSpecHash = "",
        string LastError = "",
        int Attempt = 0,
        DateTimeOffset? NextAttemptAt = null,
        string CredentialKid = "",
        bool ExposureDesired = false,
        long SourceStateVersion = 0);

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
        IReadOnlyList<ServiceEndpointHttpRequest> Endpoints,
        string? AgentKind = null);

    public sealed record ScriptingRevisionHttpRequest(
        string ScriptId,
        string Revision,
        string DefinitionActorId);

    public sealed record WorkflowRevisionHttpRequest(
        string WorkflowName,
        string WorkflowYaml,
        string? DefinitionActorId,
        IReadOnlyDictionary<string, string>? InlineWorkflowYamls,
        string? WorkflowId = null);

    public sealed record CreateRevisionHttpRequest(
        string TenantId,
        string AppId,
        string Namespace,
        string RevisionId,
        string ImplementationKind,
        StaticRevisionHttpRequest? Static,
        ScriptingRevisionHttpRequest? Scripting,
        WorkflowRevisionHttpRequest? Workflow,
        IReadOnlyList<NyxIdExplicitRequestConfirmationInput>? ExplicitRequestConfirmations = null);

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
