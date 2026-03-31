using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Serialization;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ScopeServiceEndpoints
{
    public static IEndpointRouteBuilder MapScopeServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("ScopeServices").RequireAuthorization();
        group.MapPost("/{scopeId}/workflow/draft-run", HandleDraftRunAsync);
        group.MapPut("/{scopeId}/binding", HandleUpsertBindingAsync);
        group.MapGet("/{scopeId}/binding", HandleGetBindingAsync);
        group.MapPost("/{scopeId}/binding/revisions/{revisionId}:activate", HandleActivateBindingRevisionAsync);
        group.MapGet("/{scopeId}/revisions", HandleGetDefaultServiceRevisionsAsync);
        group.MapGet("/{scopeId}/revisions/{revisionId}", HandleGetDefaultServiceRevisionAsync);
        group.MapPost("/{scopeId}/binding/revisions/{revisionId}:retire", HandleRetireBindingRevisionAsync);
        group.MapPost("/{scopeId}/invoke/chat:stream", HandleInvokeDefaultChatStreamAsync);
        group.MapPost("/{scopeId}/invoke/{endpointId}", HandleInvokeDefaultAsync);
        group.MapGet("/{scopeId}/runs", HandleListDefaultRunsAsync);
        group.MapGet("/{scopeId}/runs/{runId}", HandleGetDefaultRunAsync);
        group.MapGet("/{scopeId}/runs/{runId}/audit", HandleGetDefaultRunAuditAsync);
        group.MapPost("/{scopeId}/runs/{runId}:resume", HandleResumeDefaultRunAsync);
        group.MapPost("/{scopeId}/runs/{runId}:signal", HandleSignalDefaultRunAsync);
        group.MapPost("/{scopeId}/runs/{runId}:stop", HandleStopDefaultRunAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/invoke/{endpointId}:stream", HandleInvokeStreamAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/invoke/{endpointId}", HandleInvokeAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/revisions", HandleGetServiceRevisionsAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/revisions/{revisionId}", HandleGetServiceRevisionAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/revisions/{revisionId}:retire", HandleRetireServiceRevisionAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/runs", HandleListRunsAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/runs/{runId}", HandleGetRunAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/runs/{runId}/audit", HandleGetRunAuditAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/runs/{runId}:resume", HandleResumeRunAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/runs/{runId}:signal", HandleSignalRunAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/runs/{runId}:stop", HandleStopRunAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/bindings", HandleCreateBindingAsync);
        group.MapPut("/{scopeId}/services/{serviceId}/bindings/{bindingId}", HandleUpdateBindingAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/bindings/{bindingId}:retire", HandleRetireBindingAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/bindings", HandleGetBindingsAsync);
        return app;
    }

    private static async Task HandleDraftRunAsync(
        HttpContext http,
        string scopeId,
        ScopeDraftRunHttpRequest request,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
    {
        try
        {
            if (await ScopeEndpointAccess.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            if (request.WorkflowYamls == null || request.WorkflowYamls.Count == 0)
                throw new InvalidOperationException("workflowYamls is required.");

            var scopedHeaders = BuildScopedHeaders(scopeId, request.Headers, http);
            if (!ScopeWorkflowEndpoints.TryParseEventFormat(request.EventFormat, out var eventFormat))
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "INVALID_SCOPE_DRAFT_RUN_REQUEST",
                    "eventFormat must be either 'workflow' or 'agui'.",
                    ct);
                return;
            }

            var chatRequest = new WorkflowChatRunRequest(
                Prompt: request.Prompt?.Trim() ?? string.Empty,
                WorkflowName: null,
                ActorId: null,
                SessionId: request.SessionId,
                WorkflowYamls: request.WorkflowYamls,
                Metadata: scopedHeaders,
                ScopeId: scopeId);

            if (eventFormat == ScopeWorkflowEndpoints.ScopeWorkflowStreamEventFormat.Agui)
            {
                await ScopeWorkflowEndpoints.HandleAguiStreamAsync(
                    http,
                    chatRequest,
                    chatRunService,
                    ct);
                return;
            }

            await WorkflowCapabilityEndpoints.HandleChat(
                http,
                new ChatInput
                {
                    Prompt = chatRequest.Prompt,
                    WorkflowYamls = chatRequest.WorkflowYamls,
                    SessionId = chatRequest.SessionId,
                    ScopeId = scopeId,
                    Metadata = scopedHeaders,
                },
                chatRunService,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_SCOPE_DRAFT_RUN_REQUEST",
                ex.Message,
                ct);
        }
    }

    private static async Task<IResult> HandleUpsertBindingAsync(
        HttpContext http,
        string scopeId,
        UpsertScopeBindingHttpRequest request,
        [FromServices] IScopeBindingCommandPort commandPort,
        CancellationToken ct)
    {
        try
        {
            if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var result = await commandPort.UpsertAsync(
                new ScopeBindingUpsertRequest(
                    scopeId,
                    ParseScopeBindingImplementationKind(request.ImplementationKind),
                    ToWorkflowSpec(request),
                    request.Script == null
                        ? null
                        : new ScopeBindingScriptSpec(
                            request.Script.ScriptId,
                            request.Script.ScriptRevision),
                    request.GAgent == null
                        ? null
                        : new ScopeBindingGAgentSpec(
                            request.GAgent.ActorTypeName,
                            request.GAgent.PreferredActorId,
                            (request.GAgent.Endpoints ?? [])
                            .Select(endpoint => new ScopeBindingGAgentEndpoint(
                                endpoint.EndpointId,
                                endpoint.DisplayName,
                                ParseEndpointKind(endpoint.Kind),
                                endpoint.RequestTypeUrl,
                                endpoint.ResponseTypeUrl,
                                endpoint.Description))
                            .ToArray()),
                    request.DisplayName,
                    request.RevisionId,
                    request.AppId,
                    request.ServiceId),
                ct);
            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_BINDING_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleGetBindingAsync(
        HttpContext http,
        string scopeId,
        string? appId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceServingQueryPort servingQueryPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var identity = BuildScopeServiceIdentity(
            options.Value,
            normalizedScopeId,
            ResolveDefaultScopeServiceId(options.Value),
            appId);
        var service = await lifecycleQueryPort.GetServiceAsync(identity, ct);
        if (service == null)
        {
            return Results.Ok(new ScopeBindingStatusHttpResponse(
                false,
                normalizedScopeId,
                identity.ServiceId,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                [],
                0,
                string.Empty));
        }

        var revisions = await lifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
        var servingSet = await servingQueryPort.GetServiceServingSetAsync(identity, ct);
        return Results.Ok(BuildScopeBindingStatusResponse(normalizedScopeId, service, revisions, servingSet));
    }

    private static async Task<IResult> HandleActivateBindingRevisionAsync(
        HttpContext http,
        string scopeId,
        string revisionId,
        string? appId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceCommandPort commandPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
            var normalizedRevisionId = ScopeWorkflowCapabilityOptions.NormalizeRequired(revisionId, nameof(revisionId));
            var identity = BuildScopeServiceIdentity(
                options.Value,
                normalizedScopeId,
                ResolveDefaultScopeServiceId(options.Value),
                appId);
            var service = await lifecycleQueryPort.GetServiceAsync(identity, ct);
            if (service == null)
            {
                return Results.NotFound(new
                {
                    code = "SCOPE_BINDING_NOT_FOUND",
                    message = $"Scope '{normalizedScopeId}' has no active binding.",
                });
            }

            var revisions = await lifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
            var revision = revisions?.Revisions.FirstOrDefault(x =>
                string.Equals(x.RevisionId, normalizedRevisionId, StringComparison.Ordinal));
            if (revision == null)
            {
                return Results.NotFound(new
                {
                    code = "SCOPE_BINDING_REVISION_NOT_FOUND",
                    message = $"Revision '{normalizedRevisionId}' was not found for scope '{normalizedScopeId}'.",
                });
            }

            if (string.Equals(revision.Status, ServiceRevisionStatus.Retired.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    code = "SCOPE_BINDING_REVISION_RETIRED",
                    message = $"Revision '{normalizedRevisionId}' is retired and cannot be activated.",
                });
            }

            await commandPort.SetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
            {
                Identity = identity.Clone(),
                RevisionId = normalizedRevisionId,
            }, ct);
            await commandPort.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
            {
                Identity = identity.Clone(),
                RevisionId = normalizedRevisionId,
            }, ct);

            return Results.Ok(new ScopeBindingActivationHttpResponse(
                normalizedScopeId,
                identity.ServiceId,
                service.DisplayName,
                normalizedRevisionId));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_BINDING_ACTIVATION_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static Task<IResult> HandleGetDefaultServiceRevisionsAsync(
        HttpContext http,
        string scopeId,
        string? appId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceServingQueryPort servingQueryPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleGetServiceRevisionsAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            lifecycleQueryPort,
            servingQueryPort,
            options,
            ct,
            appId);

    private static Task<IResult> HandleGetDefaultServiceRevisionAsync(
        HttpContext http,
        string scopeId,
        string revisionId,
        string? appId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceServingQueryPort servingQueryPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleGetServiceRevisionAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            revisionId,
            lifecycleQueryPort,
            servingQueryPort,
            options,
            ct,
            appId);

    private static Task<IResult> HandleRetireBindingRevisionAsync(
        HttpContext http,
        string scopeId,
        string revisionId,
        string? appId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceCommandPort commandPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleRetireServiceRevisionAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            revisionId,
            lifecycleQueryPort,
            commandPort,
            options,
            ct,
            appId);

    private static async Task<IResult> HandleGetServiceRevisionsAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceServingQueryPort servingQueryPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct,
        string? appId = null)
    {
        var resolution = await ResolveScopeServiceAsync(http, scopeId, serviceId, lifecycleQueryPort, options.Value, ct, appId);
        if (resolution.Failure != null)
            return resolution.Failure;

        var revisions = await lifecycleQueryPort.GetServiceRevisionsAsync(resolution.Identity!, ct);
        var servingSet = await servingQueryPort.GetServiceServingSetAsync(resolution.Identity!, ct);
        return Results.Ok(BuildScopeServiceRevisionCatalogResponse(scopeId, resolution.Service!, revisions, servingSet));
    }

    private static async Task<IResult> HandleGetServiceRevisionAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string revisionId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceServingQueryPort servingQueryPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct,
        string? appId = null)
    {
        var resolution = await ResolveScopeServiceAsync(http, scopeId, serviceId, lifecycleQueryPort, options.Value, ct, appId);
        if (resolution.Failure != null)
            return resolution.Failure;

        var revisions = await lifecycleQueryPort.GetServiceRevisionsAsync(resolution.Identity!, ct);
        var servingSet = await servingQueryPort.GetServiceServingSetAsync(resolution.Identity!, ct);
        var revision = BuildScopeRevisionResponses(resolution.Service!, revisions, servingSet)
            .FirstOrDefault(x => string.Equals(x.RevisionId, revisionId?.Trim(), StringComparison.Ordinal));
        if (revision == null)
        {
            return Results.NotFound(new
            {
                code = "SCOPE_SERVICE_REVISION_NOT_FOUND",
                message = $"Revision '{revisionId}' was not found on service '{serviceId}' in scope '{scopeId}'.",
            });
        }

        return Results.Ok(revision);
    }

    private static async Task<IResult> HandleRetireServiceRevisionAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string revisionId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceCommandPort commandPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct,
        string? appId = null)
    {
        try
        {
            var resolution = await ResolveScopeServiceAsync(http, scopeId, serviceId, lifecycleQueryPort, options.Value, ct, appId);
            if (resolution.Failure != null)
                return resolution.Failure;

            var normalizedRevisionId = ScopeWorkflowCapabilityOptions.NormalizeRequired(revisionId, nameof(revisionId));
            var revisions = await lifecycleQueryPort.GetServiceRevisionsAsync(resolution.Identity!, ct);
            var revision = revisions?.Revisions.FirstOrDefault(x =>
                string.Equals(x.RevisionId, normalizedRevisionId, StringComparison.Ordinal));
            if (revision == null)
            {
                return Results.NotFound(new
                {
                    code = "SCOPE_SERVICE_REVISION_NOT_FOUND",
                    message = $"Revision '{normalizedRevisionId}' was not found on service '{serviceId}' in scope '{scopeId}'.",
                });
            }

            await commandPort.RetireRevisionAsync(new RetireServiceRevisionCommand
            {
                Identity = resolution.Identity!.Clone(),
                RevisionId = normalizedRevisionId,
            }, ct);

            return Results.Ok(new ScopeServiceRevisionActionHttpResponse(
                scopeId,
                serviceId,
                normalizedRevisionId,
                "retired"));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SERVICE_REVISION_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task HandleInvokeDefaultChatStreamAsync(
        HttpContext http,
        string scopeId,
        StreamScopeServiceHttpRequest request,
        [FromServices] ServiceInvocationResolutionService resolutionService,
        [FromServices] IInvokeAdmissionAuthorizer admissionAuthorizer,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        // Try to resolve a bound default service first.
        // If none is bound, fall back to a built-in simple llm_call workflow (draft-run).
        var serviceId = ResolveDefaultScopeServiceId(options.Value);
        var identity = BuildScopeServiceIdentity(options.Value, scopeId, serviceId);
        var hasBoundService = await resolutionService.HasServiceAsync(identity, ct);

        if (hasBoundService)
        {
            await HandleInvokeStreamAsync(
                http,
                scopeId,
                serviceId,
                "chat",
                request,
                appId: null,
                resolutionService,
                admissionAuthorizer,
                chatRunService,
                actorRuntime,
                subscriptionProvider,
                options,
                ct);
            return;
        }

        // No service bound — run a built-in default chat workflow as draft-run.
        try
        {
            if (await ScopeEndpointAccess.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var scopedHeaders = BuildScopedHeaders(scopeId, request.Headers, http);
            var chatRequest = new WorkflowChatRunRequest(
                Prompt: request.Prompt?.Trim() ?? string.Empty,
                WorkflowName: null,
                ActorId: null,
                SessionId: request.SessionId,
                WorkflowYamls: [DefaultChatWorkflowYaml],
                Metadata: scopedHeaders,
                ScopeId: scopeId);

            await WorkflowCapabilityEndpoints.HandleChat(
                http,
                new ChatInput
                {
                    Prompt = chatRequest.Prompt,
                    WorkflowYamls = chatRequest.WorkflowYamls,
                    SessionId = chatRequest.SessionId,
                    ScopeId = scopeId,
                    Metadata = scopedHeaders,
                },
                chatRunService,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_SERVICE_STREAM_REQUEST",
                ex.Message,
                ct);
        }
    }

    private const string DefaultChatWorkflowYaml = """
        name: default_chat
        description: Built-in default single-turn chat.
        roles:
          - id: assistant
            name: Assistant
            system_prompt: |
              You are a helpful assistant.
        steps:
          - id: answer
            type: llm_call
            role: assistant
            parameters: {}
        """;

    private static Task<IResult> HandleInvokeDefaultAsync(
        HttpContext http,
        string scopeId,
        string endpointId,
        InvokeScopeServiceHttpRequest request,
        [FromServices] IServiceInvocationPort invocationPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleInvokeAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            endpointId,
            request,
            appId: null,
            invocationPort,
            options,
            ct);

    private static Task<IResult> HandleListDefaultRunsAsync(
        HttpContext http,
        string scopeId,
        int take,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleListRunsAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            take,
            lifecycleQueryPort,
            workflowRunBindingReader,
            workflowExecutionQueryService,
            options,
            ct);

    private static Task<IResult> HandleGetDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        string? actorId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleGetRunAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            runId,
            actorId,
            lifecycleQueryPort,
            workflowRunBindingReader,
            workflowExecutionQueryService,
            options,
            ct);

    private static Task<IResult> HandleGetDefaultRunAuditAsync(
        HttpContext http,
        string scopeId,
        string runId,
        string? actorId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleGetRunAuditAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            runId,
            actorId,
            lifecycleQueryPort,
            workflowRunBindingReader,
            workflowExecutionQueryService,
            options,
            ct);

    private static Task<IResult> HandleResumeDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        ResumeScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleResumeRunAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            runId,
            request,
            lifecycleQueryPort,
            workflowRunBindingReader,
            resumeService,
            options,
            ct);

    private static Task<IResult> HandleSignalDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        SignalScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleSignalRunAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            runId,
            request,
            lifecycleQueryPort,
            workflowRunBindingReader,
            signalService,
            options,
            ct);

    private static Task<IResult> HandleStopDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        StopScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleStopRunAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            runId,
            request,
            lifecycleQueryPort,
            workflowRunBindingReader,
            stopService,
            options,
            ct);

    private static async Task<IResult> HandleListRunsAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        int take,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceAsync(http, scopeId, serviceId, lifecycleQueryPort, options.Value, ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        var bindings = await ListScopeServiceRunsAsync(
            scopeId,
            resolution.Service!,
            resolution.Deployments,
            workflowRunBindingReader,
            take,
            ct);

        var summaries = new List<ScopeServiceRunSummaryHttpResponse>(bindings.Count);
        foreach (var binding in bindings)
        {
            summaries.Add(await BuildScopeRunSummaryAsync(
                scopeId,
                serviceId,
                binding,
                resolution.Service!,
                resolution.Deployments,
                workflowExecutionQueryService,
                ct));
        }

        return Results.Ok(new ScopeServiceRunCatalogHttpResponse(
            scopeId,
            serviceId,
            resolution.Service!.ServiceKey,
            resolution.Service.DisplayName,
            summaries));
    }

    private static async Task<IResult> HandleGetRunAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string runId,
        string? actorId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceRunAsync(
            http,
            options.Value,
            scopeId,
            serviceId,
            runId,
            actorId,
            lifecycleQueryPort,
            workflowRunBindingReader,
            ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        return Results.Ok(await BuildScopeRunSummaryAsync(
            scopeId,
            serviceId,
            resolution.Binding!,
            resolution.Service!,
            resolution.Deployments,
            workflowExecutionQueryService,
            ct));
    }

    private static async Task<IResult> HandleGetRunAuditAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string runId,
        string? actorId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceRunAsync(
            http,
            options.Value,
            scopeId,
            serviceId,
            runId,
            actorId,
            lifecycleQueryPort,
            workflowRunBindingReader,
            ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        var summary = await BuildScopeRunSummaryAsync(
            scopeId,
            serviceId,
            resolution.Binding!,
            resolution.Service!,
            resolution.Deployments,
            workflowExecutionQueryService,
            ct);
        var report = await workflowExecutionQueryService.GetActorReportAsync(resolution.Binding!.ActorId, ct);
        if (report == null)
        {
            return Results.NotFound(new
            {
                code = "SERVICE_RUN_AUDIT_NOT_FOUND",
                message = $"Audit report for run '{resolution.Binding.RunId}' was not found on service '{serviceId}' in scope '{scopeId}'.",
            });
        }

        return Results.Ok(new ScopeServiceRunAuditHttpResponse(summary, report));
    }

    private static async Task HandleInvokeStreamAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string endpointId,
        StreamScopeServiceHttpRequest request,
        string? appId,
        [FromServices] ServiceInvocationResolutionService resolutionService,
        [FromServices] IInvokeAdmissionAuthorizer admissionAuthorizer,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        [FromServices] IActorRuntime actorRuntime,
        [FromServices] IActorEventSubscriptionProvider subscriptionProvider,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (await ScopeEndpointAccess.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var normalizedPrompt = request.Prompt?.Trim() ?? string.Empty;
            var scopedHeaders = BuildScopedHeaders(scopeId, request.Headers, http);
            var invocationRequest = BuildStreamInvocationRequest(
                options.Value,
                scopeId,
                serviceId,
                endpointId,
                normalizedPrompt,
                scopedHeaders,
                request.RevisionId,
                appId);
            var target = await resolutionService.ResolveAsync(invocationRequest, ct);
            await admissionAuthorizer.AuthorizeAsync(
                target.Service.ServiceKey,
                target.Service.DeploymentId,
                target.Artifact,
                target.Endpoint,
                invocationRequest,
                ct);

            switch (target.Artifact.ImplementationKind)
            {
                case ServiceImplementationKind.Workflow:
                    EnsureWorkflowStreamTarget(target, invocationRequest);
                    await WorkflowCapabilityEndpoints.HandleChat(
                        http,
                        new ChatInput
                        {
                            Prompt = normalizedPrompt,
                            AgentId = target.Service.PrimaryActorId,
                            SessionId = request.SessionId,
                            ScopeId = scopeId,
                            Metadata = scopedHeaders,
                        },
                        chatRunService,
                        ct);
                    break;

                case ServiceImplementationKind.Static:
                    await HandleStaticGAgentChatStreamAsync(
                        http,
                        target,
                        normalizedPrompt,
                        request.SessionId,
                        scopeId,
                        actorRuntime,
                        subscriptionProvider,
                        ct);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Service implementation '{target.Artifact.ImplementationKind}' does not support SSE stream invocation.");
            }
        }
        catch (NyxIdAuthenticationRequiredException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                ex.Message,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_SERVICE_STREAM_REQUEST",
                ex.Message,
                ct);
        }
    }

    private static async Task HandleStaticGAgentChatStreamAsync(
        HttpContext http,
        ServiceInvocationResolvedTarget target,
        string prompt,
        string? sessionId,
        string scopeId,
        IActorRuntime actorRuntime,
        IActorEventSubscriptionProvider subscriptionProvider,
        CancellationToken ct)
    {
        var plan = target.Artifact.DeploymentPlan.StaticPlan;
        var preferredActorId = string.IsNullOrWhiteSpace(plan.PreferredActorId)
            ? null
            : plan.PreferredActorId.Trim();

        // Resolve the actor type and create/reuse the actor.
        var agentType = ScopeGAgentEndpoints.ResolveAgentType(plan.ActorTypeName);
        if (agentType is null)
            throw new InvalidOperationException(
                $"GAgent type '{plan.ActorTypeName}' could not be resolved.");

        IActor actor;
        if (preferredActorId is not null)
        {
            var existing = await actorRuntime.GetAsync(preferredActorId);
            actor = existing ?? await actorRuntime.CreateAsync(agentType, preferredActorId, ct);
        }
        else
        {
            actor = await actorRuntime.CreateAsync(agentType, null, ct);
        }

        var writer = new AGUISseWriter(http.Response);
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(ct);

        var runId = Guid.NewGuid().ToString("N");
        await writer.WriteAsync(new AGUIEvent
        {
            RunStarted = new RunStartedEvent { ThreadId = actor.Id, RunId = runId },
        }, ct);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ctr = ct.Register(() => tcs.TrySetCanceled());

        await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
            actor.Id,
            async envelope =>
            {
                try
                {
                    var aguiEvent = ScopeGAgentEndpoints.TryMapEnvelopeToAguiEvent(envelope);
                    if (aguiEvent is null) return;

                    await writer.WriteAsync(aguiEvent, CancellationToken.None);

                    if (aguiEvent.EventCase is AGUIEvent.EventOneofCase.RunFinished
                        or AGUIEvent.EventOneofCase.RunError
                        or AGUIEvent.EventOneofCase.TextMessageEnd)
                    {
                        tcs.TrySetResult();
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            ct);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId ?? string.Empty,
            ScopeId = scopeId,
        };

        // Forward the caller's Bearer token so NyxID-backed GAgents can pass it
        // to the NyxID LLM gateway. Other LLM providers ignore this metadata key.
        var bearerToken = ExtractBearerToken(http);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = bearerToken;

        // Forward the user's preferred model from their config so the LLM provider
        // uses the correct model (e.g. deepseek-chat) instead of a hardcoded default.
        var userConfigStore = http.RequestServices.GetService<IUserConfigStore>();
        if (userConfigStore != null)
        {
            try
            {
                var userConfig = await userConfigStore.GetAsync(ct);
                if (!string.IsNullOrWhiteSpace(userConfig.DefaultModel))
                    chatRequest.Metadata[LLMRequestMetadataKeys.ModelOverride] = userConfig.DefaultModel.Trim();
            }
            catch
            {
                // Best-effort; fall back to provider default if config unavailable.
            }
        }

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(chatRequest),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };
        await actor.HandleEventAsync(envelope, ct);

        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(120_000, ct));
        if (completedTask == tcs.Task)
        {
            if (tcs.Task.IsFaulted)
            {
                var ex = tcs.Task.Exception?.InnerException ?? tcs.Task.Exception;
                var isAuthRequired = ex is NyxIdAuthenticationRequiredException;
                await writer.WriteAsync(new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = isAuthRequired
                            ? "NyxID authentication required. Please sign in."
                            : (ex?.Message ?? "An error occurred."),
                        Code = isAuthRequired ? "authentication_required" : null,
                    },
                }, CancellationToken.None);
            }
            else
            {
                await writer.WriteAsync(new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent { ThreadId = actor.Id, RunId = runId },
                }, CancellationToken.None);
            }
        }
        else
        {
            await writer.WriteAsync(new AGUIEvent
            {
                RunError = new RunErrorEvent { Message = "GAgent service chat stream timed out." },
            }, CancellationToken.None);
        }
    }

    private static async Task<IResult> HandleInvokeAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string endpointId,
        InvokeScopeServiceHttpRequest request,
        string? appId,
        [FromServices] IServiceInvocationPort invocationPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var identity = BuildScopeServiceIdentity(options.Value, scopeId, serviceId, appId);
            var receipt = await invocationPort.InvokeAsync(new ServiceInvocationRequest
            {
                Identity = identity,
                EndpointId = endpointId?.Trim() ?? string.Empty,
                CommandId = request.CommandId?.Trim() ?? string.Empty,
                CorrelationId = request.CorrelationId?.Trim() ?? string.Empty,
                RevisionId = request.RevisionId?.Trim() ?? string.Empty,
                Payload = ServiceJsonPayloads.PackBase64(
                    request.PayloadTypeUrl?.Trim() ?? string.Empty,
                    request.PayloadBase64),
                Caller = new ServiceInvocationCaller
                {
                    ServiceKey = string.Empty,
                    TenantId = string.Empty,
                    AppId = string.Empty,
                },
            }, ct);
            return Results.Accepted($"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}", receipt);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return CreateScopeInvokeFailureResult(ex);
        }
    }

    private static async Task<IResult> HandleResumeRunAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string runId,
        ResumeScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceRunAsync(
            http,
            options.Value,
            scopeId,
            serviceId,
            runId,
            request.ActorId,
            lifecycleQueryPort,
            workflowRunBindingReader,
            ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        return await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = resolution.Binding!.ActorId,
                RunId = resolution.Binding.RunId,
                StepId = request.StepId ?? string.Empty,
                CommandId = request.CommandId,
                Approved = request.Approved,
                UserInput = request.UserInput,
                Metadata = request.Metadata,
            },
            resumeService,
            ct);
    }

    private static async Task<IResult> HandleSignalRunAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string runId,
        SignalScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceRunAsync(
            http,
            options.Value,
            scopeId,
            serviceId,
            runId,
            request.ActorId,
            lifecycleQueryPort,
            workflowRunBindingReader,
            ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        return await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = resolution.Binding!.ActorId,
                RunId = resolution.Binding.RunId,
                SignalName = request.SignalName ?? string.Empty,
                StepId = request.StepId,
                CommandId = request.CommandId,
                Payload = request.Payload,
            },
            signalService,
            ct);
    }

    private static async Task<IResult> HandleStopRunAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string runId,
        StopScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceRunAsync(
            http,
            options.Value,
            scopeId,
            serviceId,
            runId,
            request.ActorId,
            lifecycleQueryPort,
            workflowRunBindingReader,
            ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        return await WorkflowCapabilityEndpoints.HandleStop(
            new WorkflowStopInput
            {
                ActorId = resolution.Binding!.ActorId,
                RunId = resolution.Binding.RunId,
                CommandId = request.CommandId,
                Reason = request.Reason,
            },
            stopService,
            ct);
    }

    private static async Task<IResult> HandleCreateBindingAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        ScopeServiceBindingHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var receipt = await commandPort.CreateBindingAsync(new CreateServiceBindingCommand
        {
            Spec = ToBindingSpec(options.Value, scopeId, serviceId, request, request.BindingId ?? string.Empty),
        }, ct);
        return Results.Accepted($"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/bindings/{request.BindingId}", receipt);
    }

    private static async Task<IResult> HandleUpdateBindingAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string bindingId,
        ScopeServiceBindingHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var receipt = await commandPort.UpdateBindingAsync(new UpdateServiceBindingCommand
        {
            Spec = ToBindingSpec(options.Value, scopeId, serviceId, request, bindingId),
        }, ct);
        return Results.Accepted($"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/bindings/{bindingId}", receipt);
    }

    private static async Task<IResult> HandleRetireBindingAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string bindingId,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var receipt = await commandPort.RetireBindingAsync(new RetireServiceBindingCommand
        {
            Identity = BuildScopeServiceIdentity(options.Value, scopeId, serviceId),
            BindingId = bindingId?.Trim() ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/bindings/{bindingId}", receipt);
    }

    private static async Task<IResult> HandleGetBindingsAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        [FromServices] IServiceGovernanceQueryPort queryPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var snapshot = await queryPort.GetBindingsAsync(
            BuildScopeServiceIdentity(options.Value, scopeId, serviceId),
            ct);
        return snapshot == null ? Results.NotFound() : Results.Ok(snapshot);
    }

    private static async Task<ScopeServiceResolution> ResolveScopeServiceAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        IServiceLifecycleQueryPort lifecycleQueryPort,
        ScopeWorkflowCapabilityOptions options,
        CancellationToken ct,
        string? appId = null)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return new ScopeServiceResolution(null, null, null, denied);

        var identity = BuildScopeServiceIdentity(options, scopeId, serviceId, appId);
        var service = await lifecycleQueryPort.GetServiceAsync(identity, ct);
        if (service == null)
        {
            return new ScopeServiceResolution(
                identity,
                null,
                null,
                Results.NotFound(new
                {
                    code = "SCOPE_SERVICE_NOT_FOUND",
                    message = BuildScopeServiceNotFoundMessage(scopeId, serviceId),
                }));
        }

        var deployments = await lifecycleQueryPort.GetServiceDeploymentsAsync(identity, ct);
        return new ScopeServiceResolution(identity, service, deployments, null);
    }

    private static async Task<ScopeServiceRunResolution> ResolveScopeServiceRunAsync(
        HttpContext http,
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string serviceId,
        string runId,
        string? requestedActorId,
        IServiceLifecycleQueryPort lifecycleQueryPort,
        IWorkflowRunBindingReader workflowRunBindingReader,
        CancellationToken ct)
    {
        var normalizedRunId = ScopeWorkflowCapabilityOptions.NormalizeRequired(runId, nameof(runId));
        var scopeService = await ResolveScopeServiceAsync(http, scopeId, serviceId, lifecycleQueryPort, options, ct);
        if (scopeService.Failure != null)
            return new ScopeServiceRunResolution(scopeService.Identity, scopeService.Service, scopeService.Deployments, null, scopeService.Failure);

        var service = scopeService.Service!;
        var deployments = scopeService.Deployments;
        var matches = (await workflowRunBindingReader.ListByRunIdAsync(normalizedRunId, ct: ct))
            .Where(binding => IsRunBoundToScopeService(binding, scopeId, service, deployments))
            .ToList();

        var normalizedActorId = NormalizeOptional(requestedActorId);
        if (!string.IsNullOrWhiteSpace(normalizedActorId))
        {
            matches = matches
                .Where(binding => string.Equals(binding.ActorId, normalizedActorId, StringComparison.Ordinal))
                .ToList();
        }

        if (matches.Count == 0)
        {
            return new ScopeServiceRunResolution(
                scopeService.Identity,
                service,
                deployments,
                null,
                Results.NotFound(new
                {
                    code = "SERVICE_RUN_NOT_FOUND",
                    message = BuildScopeServiceRunNotFoundMessage(scopeId, serviceId, normalizedRunId),
                }));
        }

        if (matches.Count > 1)
        {
            return new ScopeServiceRunResolution(
                scopeService.Identity,
                service,
                deployments,
                null,
                Results.Conflict(new
                {
                    code = "SERVICE_RUN_AMBIGUOUS",
                    message = $"Run '{normalizedRunId}' is ambiguous for service '{serviceId}' in scope '{scopeId}'.",
                }));
        }

        return new ScopeServiceRunResolution(scopeService.Identity, service, deployments, matches[0], null);
    }

    private static ScopeBindingStatusHttpResponse BuildScopeBindingStatusResponse(
        string scopeId,
        ServiceCatalogSnapshot service,
        ServiceRevisionCatalogSnapshot? revisions,
        ServiceServingSetSnapshot? servingSet)
    {
        var revisionSnapshots = BuildScopeRevisionResponses(service, revisions, servingSet);
        return new ScopeBindingStatusHttpResponse(
            true,
            scopeId,
            service.ServiceId,
            service.DisplayName,
            service.ServiceKey,
            service.DefaultServingRevisionId,
            service.ActiveServingRevisionId,
            service.DeploymentId,
            service.DeploymentStatus,
            service.PrimaryActorId,
            service.UpdatedAt,
            revisionSnapshots,
            revisions?.StateVersion ?? 0,
            revisions?.LastEventId ?? string.Empty);
    }

    private static ScopeServiceRevisionCatalogHttpResponse BuildScopeServiceRevisionCatalogResponse(
        string scopeId,
        ServiceCatalogSnapshot service,
        ServiceRevisionCatalogSnapshot? revisions,
        ServiceServingSetSnapshot? servingSet)
    {
        return new ScopeServiceRevisionCatalogHttpResponse(
            scopeId,
            service.ServiceId,
            service.ServiceKey,
            service.DisplayName,
            service.DefaultServingRevisionId,
            service.ActiveServingRevisionId,
            service.DeploymentId,
            service.DeploymentStatus,
            service.PrimaryActorId,
            revisions?.StateVersion ?? 0,
            revisions?.LastEventId ?? string.Empty,
            revisions?.UpdatedAt ?? service.UpdatedAt,
            BuildScopeRevisionResponses(service, revisions, servingSet));
    }

    private static IReadOnlyList<ScopeBindingRevisionHttpResponse> BuildScopeRevisionResponses(
        ServiceCatalogSnapshot service,
        ServiceRevisionCatalogSnapshot? revisions,
        ServiceServingSetSnapshot? servingSet)
    {
        var servingTargetsByRevision = BuildServingTargetIndex(servingSet);
        return (revisions?.Revisions ?? [])
            .Select(revision =>
            {
                servingTargetsByRevision.TryGetValue(revision.RevisionId, out var servingTarget);
                return new ScopeBindingRevisionHttpResponse(
                    revision.RevisionId,
                    revision.ImplementationKind,
                    revision.Status,
                    revision.ArtifactHash,
                    revision.FailureReason,
                    string.Equals(service.DefaultServingRevisionId, revision.RevisionId, StringComparison.Ordinal),
                    string.Equals(service.ActiveServingRevisionId, revision.RevisionId, StringComparison.Ordinal),
                    servingTarget != null,
                    servingTarget?.AllocationWeight ?? 0,
                    servingTarget?.ServingState ?? string.Empty,
                    servingTarget?.DeploymentId ?? string.Empty,
                    servingTarget?.PrimaryActorId ?? string.Empty,
                    revision.CreatedAt,
                    revision.PreparedAt,
                    revision.PublishedAt,
                    revision.RetiredAt,
                    revision.Implementation?.Workflow?.WorkflowName ?? string.Empty,
                    revision.Implementation?.Workflow?.DefinitionActorId ?? string.Empty,
                    revision.Implementation?.Workflow?.InlineWorkflowCount ?? 0,
                    revision.Implementation?.Scripting?.ScriptId ?? string.Empty,
                    revision.Implementation?.Scripting?.Revision ?? string.Empty,
                    revision.Implementation?.Scripting?.DefinitionActorId ?? string.Empty,
                    revision.Implementation?.Scripting?.SourceHash ?? string.Empty,
                    revision.Implementation?.Static?.ActorTypeName ?? string.Empty,
                    revision.Implementation?.Static?.PreferredActorId ?? string.Empty);
            })
            .OrderByDescending(x => x.IsDefaultServing)
            .ThenByDescending(x => x.IsActiveServing)
            .ThenByDescending(x => x.PublishedAt ?? x.CreatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private static async Task<IReadOnlyList<WorkflowActorBinding>> ListScopeServiceRunsAsync(
        string scopeId,
        ServiceCatalogSnapshot service,
        ServiceDeploymentCatalogSnapshot? deployments,
        IWorkflowRunBindingReader workflowRunBindingReader,
        int take,
        CancellationToken ct)
    {
        var definitionActorIds = BuildDefinitionActorIdSet(service, deployments).ToArray();
        if (definitionActorIds.Length == 0)
            return [];

        return await workflowRunBindingReader.QueryAsync(
            new WorkflowRunBindingQuery(scopeId, definitionActorIds, Math.Clamp(take, 1, 200)),
            ct);
    }

    private static async Task<ScopeServiceRunSummaryHttpResponse> BuildScopeRunSummaryAsync(
        string scopeId,
        string serviceId,
        WorkflowActorBinding binding,
        ServiceCatalogSnapshot service,
        ServiceDeploymentCatalogSnapshot? deployments,
        IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        CancellationToken ct)
    {
        var snapshot = await workflowExecutionQueryService.GetActorSnapshotAsync(binding.ActorId, ct);
        var deployment = ResolveRunDeployment(binding, service, deployments);
        return new ScopeServiceRunSummaryHttpResponse(
            scopeId,
            serviceId,
            binding.RunId,
            binding.ActorId,
            binding.EffectiveDefinitionActorId,
            deployment?.RevisionId ?? string.Empty,
            deployment?.DeploymentId ?? string.Empty,
            string.IsNullOrWhiteSpace(binding.WorkflowName)
                ? snapshot?.WorkflowName ?? string.Empty
                : binding.WorkflowName,
            snapshot?.CompletionStatus ?? WorkflowRunCompletionStatus.Unknown,
            snapshot?.StateVersion ?? 0,
            snapshot?.LastEventId ?? string.Empty,
            snapshot?.LastUpdatedAt,
            binding.CreatedAt,
            binding.UpdatedAt,
            snapshot?.LastSuccess,
            snapshot?.TotalSteps ?? 0,
            snapshot?.CompletedSteps ?? 0,
            snapshot?.RoleReplyCount ?? 0,
            snapshot?.LastOutput ?? string.Empty,
            snapshot?.LastError ?? string.Empty);
    }

    private static ServiceDeploymentSnapshot? ResolveRunDeployment(
        WorkflowActorBinding binding,
        ServiceCatalogSnapshot service,
        ServiceDeploymentCatalogSnapshot? deployments)
    {
        var definitionActorId = binding.EffectiveDefinitionActorId;
        if (deployments != null)
        {
            var match = deployments.Deployments.FirstOrDefault(x =>
                string.Equals(x.PrimaryActorId, definitionActorId, StringComparison.Ordinal));
            if (match != null)
                return match;
        }

        if (string.Equals(service.PrimaryActorId, definitionActorId, StringComparison.Ordinal))
        {
            return new ServiceDeploymentSnapshot(
                service.DeploymentId,
                service.ActiveServingRevisionId,
                service.PrimaryActorId,
                service.DeploymentStatus,
                service.UpdatedAt,
                service.UpdatedAt);
        }

        return null;
    }

    private static HashSet<string> BuildDefinitionActorIdSet(
        ServiceCatalogSnapshot service,
        ServiceDeploymentCatalogSnapshot? deployments)
    {
        var definitionActorIds = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(service.PrimaryActorId))
            definitionActorIds.Add(service.PrimaryActorId);
        if (deployments != null)
        {
            foreach (var deployment in deployments.Deployments)
            {
                if (!string.IsNullOrWhiteSpace(deployment.PrimaryActorId))
                    definitionActorIds.Add(deployment.PrimaryActorId);
            }
        }

        return definitionActorIds;
    }

    private static ServiceBindingSpec ToBindingSpec(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string serviceId,
        ScopeServiceBindingHttpRequest request,
        string bindingId)
    {
        var spec = new ServiceBindingSpec
        {
            Identity = BuildScopeServiceIdentity(options, scopeId, serviceId),
            BindingId = bindingId?.Trim() ?? string.Empty,
            DisplayName = request.DisplayName?.Trim() ?? string.Empty,
            BindingKind = ParseBindingKind(request.BindingKind),
        };
        spec.PolicyIds.Add(request.PolicyIds ?? []);

        switch (spec.BindingKind)
        {
            case ServiceBindingKind.Service:
                spec.ServiceRef = new BoundServiceRef
                {
                    Identity = BuildScopeServiceIdentity(options, scopeId, request.Service?.ServiceId ?? string.Empty),
                    EndpointId = request.Service?.EndpointId?.Trim() ?? string.Empty,
                };
                break;
            case ServiceBindingKind.Connector:
                spec.ConnectorRef = new BoundConnectorRef
                {
                    ConnectorType = request.Connector?.ConnectorType?.Trim() ?? string.Empty,
                    ConnectorId = request.Connector?.ConnectorId?.Trim() ?? string.Empty,
                };
                break;
            case ServiceBindingKind.Secret:
                spec.SecretRef = new BoundSecretRef
                {
                    SecretName = request.Secret?.SecretName?.Trim() ?? string.Empty,
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

    private static ServiceEndpointKind ParseEndpointKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "chat" => ServiceEndpointKind.Chat,
            "command" or null or "" => ServiceEndpointKind.Command,
            _ => throw new InvalidOperationException($"Unsupported endpoint kind '{rawValue}'."),
        };
    }

    private static ServiceInvocationRequest BuildStreamInvocationRequest(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string serviceId,
        string endpointId,
        string prompt,
        IReadOnlyDictionary<string, string>? headers,
        string? revisionId,
        string? appId = null)
    {
        var payload = new ChatRequestEvent
        {
            Prompt = prompt,
            ScopeId = scopeId,
        };
        if (headers != null)
        {
            foreach (var (key, value) in headers)
                payload.Metadata[key] = value;
        }

        return new ServiceInvocationRequest
        {
            Identity = BuildScopeServiceIdentity(options, scopeId, serviceId, appId),
            EndpointId = endpointId?.Trim() ?? string.Empty,
            RevisionId = revisionId?.Trim() ?? string.Empty,
            Payload = Any.Pack(payload),
            Caller = new ServiceInvocationCaller
            {
                ServiceKey = string.Empty,
                TenantId = string.Empty,
                AppId = string.Empty,
            },
        };
    }

    private static void EnsureWorkflowStreamTarget(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request)
    {
        if (target.Artifact.ImplementationKind != ServiceImplementationKind.Workflow)
            throw new InvalidOperationException("Only workflow services support SSE stream execution.");
        if (target.Endpoint.Kind != ServiceEndpointKind.Chat)
            throw new InvalidOperationException("Only chat endpoints support SSE stream execution.");
        if (!string.IsNullOrWhiteSpace(target.Endpoint.RequestTypeUrl) &&
            !string.Equals(target.Endpoint.RequestTypeUrl, request.Payload?.TypeUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Endpoint '{target.Endpoint.EndpointId}' expects payload '{target.Endpoint.RequestTypeUrl}', but got '{request.Payload?.TypeUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(target.Service.PrimaryActorId))
            throw new InvalidOperationException("Workflow service has no active definition actor.");
    }

    private static Dictionary<string, string> BuildScopedHeaders(
        string scopeId,
        IReadOnlyDictionary<string, string>? headers,
        HttpContext? http = null)
    {
        var scopedHeaders = headers == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        scopedHeaders.Remove("scope_id");
        scopedHeaders.Remove(WorkflowRunCommandMetadataKeys.ScopeId);
        InjectBearerToken(http, scopedHeaders);
        return scopedHeaders;
    }

    private static void InjectBearerToken(HttpContext? http, Dictionary<string, string> metadata)
    {
        if (http == null)
            return;
        var auth = http.Request.Headers.Authorization.FirstOrDefault();
        if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            metadata["nyxid.access_token"] = auth["Bearer ".Length..].Trim();
    }

    private static bool IsRunBoundToScopeService(
        WorkflowActorBinding binding,
        string scopeId,
        ServiceCatalogSnapshot service,
        ServiceDeploymentCatalogSnapshot? deployments)
    {
        if (binding.ActorKind != WorkflowActorKind.Run ||
            string.IsNullOrWhiteSpace(binding.ActorId) ||
            string.IsNullOrWhiteSpace(binding.EffectiveDefinitionActorId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(binding.ScopeId) &&
            !string.Equals(binding.ScopeId, scopeId, StringComparison.Ordinal))
        {
            return false;
        }

        return BuildDefinitionActorIdSet(service, deployments)
            .Contains(binding.EffectiveDefinitionActorId);
    }

    private static IReadOnlyDictionary<string, ServiceServingTargetSnapshot> BuildServingTargetIndex(
        ServiceServingSetSnapshot? servingSet)
    {
        if (servingSet == null)
            return new Dictionary<string, ServiceServingTargetSnapshot>(StringComparer.Ordinal);

        return servingSet.Targets
            .GroupBy(x => x.RevisionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    // Prefer the most live target for summary output before comparing weights.
                    .OrderByDescending(GetServingStateSummaryPriority)
                    .ThenByDescending(x => x.AllocationWeight)
                    .ThenBy(x => x.DeploymentId, StringComparer.Ordinal)
                    .ThenBy(x => x.PrimaryActorId, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);
    }

    private static IResult CreateScopeInvokeFailureResult(Exception ex)
    {
        if (ex is FormatException)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_SERVICE_INVOKE_REQUEST",
                message = "payloadBase64 must be valid base64.",
            });
        }

        var message = ex.Message;
        if (IsScopeInvokeNotFoundFailure(message))
        {
            return Results.NotFound(new
            {
                code = "SCOPE_SERVICE_INVOKE_TARGET_NOT_FOUND",
                message,
            });
        }

        if (IsScopeInvokeUnavailableFailure(message))
        {
            return Results.Conflict(new
            {
                code = "SCOPE_SERVICE_INVOKE_TARGET_UNAVAILABLE",
                message,
            });
        }

        return Results.BadRequest(new
        {
            code = "INVALID_SCOPE_SERVICE_INVOKE_REQUEST",
            message,
        });
    }

    private static bool IsScopeInvokeNotFoundFailure(string message) =>
        message.Contains(" was not found.", StringComparison.Ordinal) ||
        message.Contains(" was not found on service ", StringComparison.Ordinal) ||
        message.Contains(" has no serving target on service ", StringComparison.Ordinal);

    private static bool IsScopeInvokeUnavailableFailure(string message) =>
        message.Contains("has no serving traffic view", StringComparison.Ordinal) ||
        message.Contains("has no serving target", StringComparison.Ordinal) ||
        message.Contains("No active serving targets are available.", StringComparison.Ordinal) ||
        message.Contains("Prepared artifact", StringComparison.Ordinal) ||
        message.Contains(" is not active on service ", StringComparison.Ordinal);

    private static int GetServingStateSummaryPriority(ServiceServingTargetSnapshot target)
    {
        if (!System.Enum.TryParse<ServiceServingState>(target.ServingState, ignoreCase: true, out var state))
            return 0;

        return state switch
        {
            ServiceServingState.Active => 5,
            ServiceServingState.Paused => 4,
            ServiceServingState.Draining => 3,
            ServiceServingState.Disabled => 2,
            ServiceServingState.Unspecified => 1,
            _ => 0,
        };
    }

    private static ServiceIdentity BuildScopeServiceIdentity(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string serviceId,
        string? appId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalizedAppId = appId?.Trim() ?? string.Empty;
        return new ServiceIdentity
        {
            TenantId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId)),
            AppId = string.IsNullOrWhiteSpace(normalizedAppId)
                ? ScopeWorkflowCapabilityOptions.NormalizeRequired(options.ServiceAppId, nameof(options.ServiceAppId))
                : normalizedAppId,
            Namespace = ScopeWorkflowCapabilityOptions.NormalizeRequired(options.ServiceNamespace, nameof(options.ServiceNamespace)),
            ServiceId = ScopeWorkflowCapabilityOptions.NormalizeRequired(serviceId, nameof(serviceId)),
        };
    }

    private static string ResolveDefaultScopeServiceId(ScopeWorkflowCapabilityOptions options) =>
        ScopeWorkflowCapabilityOptions.NormalizeRequired(options.DefaultServiceId, nameof(options.DefaultServiceId));

    private static ScopeBindingImplementationKind ParseScopeBindingImplementationKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "workflow" => ScopeBindingImplementationKind.Workflow,
            "script" => ScopeBindingImplementationKind.Scripting,
            "scripting" => ScopeBindingImplementationKind.Scripting,
            "gagent" => ScopeBindingImplementationKind.GAgent,
            _ => throw new InvalidOperationException($"Unsupported implementationKind '{rawValue}'."),
        };
    }

    private static ScopeBindingWorkflowSpec? ToWorkflowSpec(UpsertScopeBindingHttpRequest request)
    {
        var workflowYamls = request.Workflow?.WorkflowYamls ?? request.WorkflowYamls;
        return workflowYamls == null ? null : new ScopeBindingWorkflowSpec(workflowYamls);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string BuildScopeServiceNotFoundMessage(string scopeId, string serviceId) =>
        $"Service '{serviceId}' was not found in scope '{scopeId}'.";

    private static string BuildScopeServiceRunNotFoundMessage(string scopeId, string serviceId, string runId) =>
        $"Run '{runId}' was not found on service '{serviceId}' in scope '{scopeId}'.";

    private static async Task WriteJsonErrorResponseAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(new { code, message }, cancellationToken: ct);
    }

    private static string? ExtractBearerToken(HttpContext http)
    {
        var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
            return null;

        return authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : null;
    }

    public sealed record InvokeScopeServiceHttpRequest(
        string? CommandId,
        string? CorrelationId,
        string? PayloadTypeUrl,
        string? PayloadBase64,
        string? RevisionId = null);

    public sealed record ScopeDraftRunHttpRequest(
        string Prompt,
        IReadOnlyList<string>? WorkflowYamls,
        string? SessionId = null,
        Dictionary<string, string>? Headers = null,
        string? EventFormat = null);

    public sealed record UpsertScopeBindingHttpRequest(
        string ImplementationKind,
        IReadOnlyList<string>? WorkflowYamls = null,
        ScopeBindingWorkflowHttpRequest? Workflow = null,
        ScopeBindingScriptHttpRequest? Script = null,
        ScopeBindingGAgentHttpRequest? GAgent = null,
        string? DisplayName = null,
        string? RevisionId = null,
        string? AppId = null,
        string? ServiceId = null);

    public sealed record ScopeBindingWorkflowHttpRequest(
        IReadOnlyList<string>? WorkflowYamls);

    public sealed record ScopeBindingScriptHttpRequest(
        string ScriptId,
        string? ScriptRevision = null);

    public sealed record ScopeBindingGAgentHttpRequest(
        string ActorTypeName,
        string? PreferredActorId,
        IReadOnlyList<ServiceEndpoints.ServiceEndpointHttpRequest>? Endpoints);

    public sealed record StreamScopeServiceHttpRequest(
        string Prompt,
        string? SessionId = null,
        Dictionary<string, string>? Headers = null,
        string? RevisionId = null);

    public sealed record ResumeScopeServiceRunHttpRequest(
        string? StepId,
        string? CommandId,
        bool Approved,
        string? UserInput = null,
        Dictionary<string, string>? Metadata = null,
        string? ActorId = null);

    public sealed record SignalScopeServiceRunHttpRequest(
        string? SignalName,
        string? StepId = null,
        string? CommandId = null,
        string? Payload = null,
        string? ActorId = null);

    public sealed record StopScopeServiceRunHttpRequest(
        string? Reason = null,
        string? CommandId = null,
        string? ActorId = null);

    public sealed record BoundScopeServiceHttpRequest(
        string ServiceId,
        string? EndpointId = null);

    public sealed record BoundConnectorHttpRequest(
        string ConnectorType,
        string ConnectorId);

    public sealed record BoundSecretHttpRequest(
        string SecretName);

    public sealed record ScopeServiceBindingHttpRequest(
        string? BindingId,
        string? DisplayName,
        string BindingKind,
        BoundScopeServiceHttpRequest? Service,
        BoundConnectorHttpRequest? Connector,
        BoundSecretHttpRequest? Secret,
        IReadOnlyList<string>? PolicyIds = null);

    private sealed record ScopeServiceResolution(
        ServiceIdentity? Identity,
        ServiceCatalogSnapshot? Service,
        ServiceDeploymentCatalogSnapshot? Deployments,
        IResult? Failure);

    private sealed record ScopeServiceRunResolution(
        ServiceIdentity? Identity,
        ServiceCatalogSnapshot? Service,
        ServiceDeploymentCatalogSnapshot? Deployments,
        WorkflowActorBinding? Binding,
        IResult? Failure);

    public sealed record ScopeBindingStatusHttpResponse(
        bool Available,
        string ScopeId,
        string ServiceId,
        string DisplayName,
        string ServiceKey,
        string DefaultServingRevisionId,
        string ActiveServingRevisionId,
        string DeploymentId,
        string DeploymentStatus,
        string PrimaryActorId,
        DateTimeOffset? UpdatedAt,
        IReadOnlyList<ScopeBindingRevisionHttpResponse> Revisions,
        long CatalogStateVersion = 0,
        string CatalogLastEventId = "");

    public sealed record ScopeBindingRevisionHttpResponse(
        string RevisionId,
        string ImplementationKind,
        string Status,
        string ArtifactHash,
        string FailureReason,
        bool IsDefaultServing,
        bool IsActiveServing,
        bool IsServingTarget,
        int AllocationWeight,
        string ServingState,
        string DeploymentId,
        string PrimaryActorId,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? PreparedAt,
        DateTimeOffset? PublishedAt,
        DateTimeOffset? RetiredAt,
        string WorkflowName = "",
        string WorkflowDefinitionActorId = "",
        int InlineWorkflowCount = 0,
        string ScriptId = "",
        string ScriptRevision = "",
        string ScriptDefinitionActorId = "",
        string ScriptSourceHash = "",
        string StaticActorTypeName = "",
        string StaticPreferredActorId = "");

    public sealed record ScopeBindingActivationHttpResponse(
        string ScopeId,
        string ServiceId,
        string DisplayName,
        string RevisionId);

    public sealed record ScopeServiceRevisionCatalogHttpResponse(
        string ScopeId,
        string ServiceId,
        string ServiceKey,
        string DisplayName,
        string DefaultServingRevisionId,
        string ActiveServingRevisionId,
        string DeploymentId,
        string DeploymentStatus,
        string PrimaryActorId,
        long CatalogStateVersion,
        string CatalogLastEventId,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<ScopeBindingRevisionHttpResponse> Revisions);

    public sealed record ScopeServiceRevisionActionHttpResponse(
        string ScopeId,
        string ServiceId,
        string RevisionId,
        string Status);

    public sealed record ScopeServiceRunCatalogHttpResponse(
        string ScopeId,
        string ServiceId,
        string ServiceKey,
        string DisplayName,
        IReadOnlyList<ScopeServiceRunSummaryHttpResponse> Runs);

    public sealed record ScopeServiceRunSummaryHttpResponse(
        string ScopeId,
        string ServiceId,
        string RunId,
        string ActorId,
        string DefinitionActorId,
        string RevisionId,
        string DeploymentId,
        string WorkflowName,
        WorkflowRunCompletionStatus CompletionStatus,
        long StateVersion,
        string LastEventId,
        DateTimeOffset? LastUpdatedAt,
        DateTimeOffset? BoundAt,
        DateTimeOffset? BindingUpdatedAt,
        bool? LastSuccess,
        int TotalSteps,
        int CompletedSteps,
        int RoleReplyCount,
        string LastOutput,
        string LastError);

    public sealed record ScopeServiceRunAuditHttpResponse(
        ScopeServiceRunSummaryHttpResponse Summary,
        WorkflowRunReport Audit);
}
