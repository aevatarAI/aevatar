using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.Hosting;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Ports;
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
using System.Text.Json;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ScopeServiceEndpoints
{
    private const string DefaultScopeServiceSmokePrompt = "Hello from Studio Bind.";
    private const string StreamFrameFormatWorkflow = "workflow-run-event";
    private const string StreamFrameFormatAgui = "agui";
    private static readonly JsonSerializerOptions PrettyJsonSerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static IEndpointRouteBuilder MapScopeServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = ScopeEndpointRouteGroups.MapScopeGroup(app).WithTags("ScopeServices");
        group.MapPost("/{scopeId}/workflow/draft-run", HandleDraftRunAsync);
        group.MapPut("/{scopeId}/binding", HandleUpsertBindingAsync);
        group.MapGet("/{scopeId}/binding", HandleGetBindingAsync);
        group.MapGet("/{scopeId}/members/{memberId}/published-service", HandleGetMemberPublishedServiceAsync);
        group.MapPost("/{scopeId}/binding/revisions/{revisionId}:activate", HandleActivateBindingRevisionAsync);
        group.MapGet("/{scopeId}/revisions", HandleGetDefaultServiceRevisionsAsync);
        group.MapGet("/{scopeId}/revisions/{revisionId}", HandleGetDefaultServiceRevisionAsync);
        group.MapPost("/{scopeId}/binding/revisions/{revisionId}:retire", HandleRetireBindingRevisionAsync);
        group.MapPost("/{scopeId}/invoke/chat:stream", HandleInvokeDefaultChatStreamAsync);
        group.MapPost("/{scopeId}/invoke/{endpointId}", HandleInvokeDefaultAsync);
        group.MapPost("/{scopeId}/members/{memberId}/invoke/{endpointId}:stream", HandleInvokeMemberStreamAsync);
        group.MapPost("/{scopeId}/members/{memberId}/invoke/{endpointId}", HandleInvokeMemberAsync);
        group.MapGet("/{scopeId}/runs", HandleListDefaultRunsAsync);
        group.MapGet("/{scopeId}/runs/{runId}", HandleGetDefaultRunAsync);
        group.MapGet("/{scopeId}/members/{memberId}/runs", HandleListMemberRunsAsync);
        group.MapGet("/{scopeId}/members/{memberId}/runs/{runId}", HandleGetMemberRunAsync);
        group.MapGet("/{scopeId}/members/{memberId}/runs/{runId}/audit", HandleGetMemberRunAuditAsync);
        group.MapPost("/{scopeId}/members/{memberId}/runs/{runId}:resume", HandleResumeMemberRunAsync);
        group.MapPost("/{scopeId}/members/{memberId}/runs/{runId}:signal", HandleSignalMemberRunAsync);
        group.MapPost("/{scopeId}/members/{memberId}/runs/{runId}:stop", HandleStopMemberRunAsync);
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
        group.MapGet("/{scopeId}/services/{serviceId}/endpoints/{endpointId}/contract", HandleGetEndpointContractAsync);
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
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            if (request.WorkflowYamls == null || request.WorkflowYamls.Count == 0)
                throw new InvalidOperationException("workflowYamls is required.");

            var scopedHeaders = await BuildScopedHeadersAsync(scopeId, request.Headers, http, ct);
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
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
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
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
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

    private static async Task<IResult> HandleGetMemberPublishedServiceAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            if (AevatarMemberAccessGuard.TryCreateMemberAccessDeniedResult(http, memberId, out var memberDenied))
                return memberDenied;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            var identity = BuildScopeServiceIdentity(
                options.Value,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId);
            return Results.Ok(BuildMemberPublishedServiceResponse(memberResolution, identity));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_MEMBER_PUBLISHED_SERVICE_REQUEST",
                message = ex.Message,
            });
        }
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
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
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
        [FromServices] IServiceRunRegistrationPort serviceRunRegistrationPort,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        [FromServices] ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> gagentDraftRunService,
        [FromServices] IScriptRuntimeCommandPort scriptRuntimeCommandPort,
        [FromServices] IScriptExecutionProjectionPort scriptExecutionProjectionPort,
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
                serviceRunRegistrationPort,
                chatRunService,
                gagentDraftRunService,
                scriptRuntimeCommandPort,
                scriptExecutionProjectionPort,
                options,
                ct);
            return;
        }

        // No service bound — run a built-in default chat workflow as draft-run.
        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var scopedHeaders = await BuildScopedHeadersAsync(scopeId, request.Headers, http, ct);
            var chatInputParts = MapInputParts(request.InputParts);
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
                    InputParts = chatInputParts,
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
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionArtifactStore artifactStore,
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
            catalogReader,
            artifactStore,
            options,
            ct);

    private static async Task HandleInvokeMemberStreamAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string endpointId,
        StreamScopeServiceHttpRequest request,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] ServiceInvocationResolutionService resolutionService,
        [FromServices] IInvokeAdmissionAuthorizer admissionAuthorizer,
        [FromServices] IServiceRunRegistrationPort serviceRunRegistrationPort,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        [FromServices] ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> gagentDraftRunService,
        [FromServices] IScriptRuntimeCommandPort scriptRuntimeCommandPort,
        [FromServices] IScriptExecutionProjectionPort scriptExecutionProjectionPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            if (await AevatarMemberAccessGuard.TryWriteMemberAccessDeniedAsync(http, memberId, ct))
                return;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            await HandleInvokeStreamAsync(
                http,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                endpointId,
                request,
                null,
                resolutionService,
                admissionAuthorizer,
                serviceRunRegistrationPort,
                chatRunService,
                gagentDraftRunService,
                scriptRuntimeCommandPort,
                scriptExecutionProjectionPort,
                options,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_MEMBER_SERVICE_STREAM_REQUEST",
                ex.Message,
                ct);
        }
    }

    private static async Task<IResult> HandleInvokeMemberAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string endpointId,
        InvokeScopeServiceHttpRequest request,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IServiceInvocationPort invocationPort,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionArtifactStore artifactStore,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            if (AevatarMemberAccessGuard.TryCreateMemberAccessDeniedResult(http, memberId, out var memberDenied))
                return memberDenied;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            return await HandleInvokeAsyncCore(
                http,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                endpointId,
                request,
                null,
                BuildMemberApiPath(memberResolution.ScopeId, memberResolution.MemberId),
                invocationPort,
                catalogReader,
                artifactStore,
                options,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return CreateScopeInvokeFailureResult(ex);
        }
    }

    private static Task<IResult> HandleListDefaultRunsAsync(
        HttpContext http,
        string scopeId,
        int take,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceRunQueryPort serviceRunQueryPort,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleListRunsAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            take,
            lifecycleQueryPort,
            serviceRunQueryPort,
            workflowExecutionQueryService,
            options,
            ct);

    private static Task<IResult> HandleGetDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        string? actorId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceRunQueryPort serviceRunQueryPort,
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
            serviceRunQueryPort,
            workflowExecutionQueryService,
            options,
            ct);

    private static Task<IResult> HandleGetDefaultRunAuditAsync(
        HttpContext http,
        string scopeId,
        string runId,
        string? actorId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceRunQueryPort serviceRunQueryPort,
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
            serviceRunQueryPort,
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

    private static async Task<IResult> HandleListMemberRunsAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        int take,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            if (AevatarMemberAccessGuard.TryCreateMemberAccessDeniedResult(http, memberId, out var memberDenied))
                return memberDenied;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            var resolution = await ResolveScopeServiceAsync(
                http,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                lifecycleQueryPort,
                options.Value,
                ct,
                appId: null);
            if (resolution.Failure != null)
                return resolution.Failure;

            var bindings = await ListScopeServiceRunsAsync(
                memberResolution.ScopeId,
                resolution.Service!,
                resolution.Deployments,
                workflowRunBindingReader,
                take,
                ct);

            var summaries = new List<MemberScopeServiceRunSummaryHttpResponse>(bindings.Count);
            foreach (var binding in bindings)
            {
                var serviceSummary = await BuildScopeRunSummaryAsync(
                    memberResolution.ScopeId,
                    memberResolution.PublishedServiceId,
                    binding,
                    resolution.Service!,
                    resolution.Deployments,
                    workflowExecutionQueryService,
                    ct);
                summaries.Add(BuildMemberRunSummaryResponse(memberResolution, serviceSummary));
            }

            return Results.Ok(new MemberScopeServiceRunCatalogHttpResponse(
                memberResolution.ScopeId,
                memberResolution.MemberId,
                memberResolution.PublishedServiceId,
                resolution.Service!.ServiceKey,
                resolution.Service.DisplayName,
                summaries));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_MEMBER_RUNS_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleGetMemberRunAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string runId,
        string? actorId,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            if (AevatarMemberAccessGuard.TryCreateMemberAccessDeniedResult(http, memberId, out var memberDenied))
                return memberDenied;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            var resolution = await ResolveScopeServiceRunAsync(
                http,
                options.Value,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                runId,
                actorId,
                lifecycleQueryPort,
                workflowRunBindingReader,
                ct,
                appId: null);
            if (resolution.Failure != null)
                return resolution.Failure;

            var serviceSummary = await BuildScopeRunSummaryAsync(
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                resolution.Binding!,
                resolution.Service!,
                resolution.Deployments,
                workflowExecutionQueryService,
                ct);
            return Results.Ok(BuildMemberRunSummaryResponse(memberResolution, serviceSummary));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_MEMBER_RUN_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleGetMemberRunAuditAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string runId,
        string? actorId,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            if (AevatarMemberAccessGuard.TryCreateMemberAccessDeniedResult(http, memberId, out var memberDenied))
                return memberDenied;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            var resolution = await ResolveScopeServiceRunAsync(
                http,
                options.Value,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                runId,
                actorId,
                lifecycleQueryPort,
                workflowRunBindingReader,
                ct,
                appId: null);
            if (resolution.Failure != null)
                return resolution.Failure;

            var serviceSummary = await BuildScopeRunSummaryAsync(
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
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
                    code = "MEMBER_SERVICE_RUN_AUDIT_NOT_FOUND",
                    message = $"Audit report for run '{resolution.Binding.RunId}' was not found for member '{memberResolution.MemberId}' in scope '{memberResolution.ScopeId}'.",
                });
            }

            return Results.Ok(new MemberScopeServiceRunAuditHttpResponse(
                BuildMemberRunSummaryResponse(memberResolution, serviceSummary),
                report));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_MEMBER_RUN_AUDIT_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleResumeMemberRunAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string runId,
        ResumeScopeServiceRunHttpRequest request,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            if (AevatarMemberAccessGuard.TryCreateMemberAccessDeniedResult(http, memberId, out var memberDenied))
                return memberDenied;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            return await HandleResumeRunAsync(
                http,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                runId,
                request,
                lifecycleQueryPort,
                workflowRunBindingReader,
                resumeService,
                options,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_MEMBER_RUN_RESUME_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleSignalMemberRunAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string runId,
        SignalScopeServiceRunHttpRequest request,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            if (AevatarMemberAccessGuard.TryCreateMemberAccessDeniedResult(http, memberId, out var memberDenied))
                return memberDenied;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            return await HandleSignalRunAsync(
                http,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                runId,
                request,
                lifecycleQueryPort,
                workflowRunBindingReader,
                signalService,
                options,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_MEMBER_RUN_SIGNAL_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleStopMemberRunAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string runId,
        StopScopeServiceRunHttpRequest request,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            if (AevatarMemberAccessGuard.TryCreateMemberAccessDeniedResult(http, memberId, out var memberDenied))
                return memberDenied;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            return await HandleStopRunAsync(
                http,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                runId,
                request,
                lifecycleQueryPort,
                workflowRunBindingReader,
                stopService,
                options,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_MEMBER_RUN_STOP_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleListRunsAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        int take,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceRunQueryPort serviceRunQueryPort,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceAsync(http, scopeId, serviceId, lifecycleQueryPort, options.Value, ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        var snapshots = await serviceRunQueryPort.ListAsync(
            new ServiceRunQuery(scopeId, serviceId, Math.Clamp(take <= 0 ? 50 : take, 1, 200)),
            ct);

        var summaries = new List<ScopeServiceRunSummaryHttpResponse>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            summaries.Add(await BuildScopeRunSummaryFromRegistryAsync(
                scopeId,
                serviceId,
                snapshot,
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
        [FromServices] IServiceRunQueryPort serviceRunQueryPort,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var serviceResolution = await ResolveScopeServiceAsync(http, scopeId, serviceId, lifecycleQueryPort, options.Value, ct);
        if (serviceResolution.Failure != null)
            return serviceResolution.Failure;

        var snapshot = await ResolveServiceRunSnapshotAsync(scopeId, serviceId, runId, serviceRunQueryPort, ct);
        if (snapshot == null)
        {
            return Results.NotFound(new
            {
                code = "SERVICE_RUN_NOT_FOUND",
                message = BuildScopeServiceRunNotFoundMessage(scopeId, serviceId, runId?.Trim() ?? string.Empty),
            });
        }

        return Results.Ok(await BuildScopeRunSummaryFromRegistryAsync(
            scopeId,
            serviceId,
            snapshot,
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
        [FromServices] IServiceRunQueryPort serviceRunQueryPort,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var serviceResolution = await ResolveScopeServiceAsync(http, scopeId, serviceId, lifecycleQueryPort, options.Value, ct);
        if (serviceResolution.Failure != null)
            return serviceResolution.Failure;

        var snapshot = await ResolveServiceRunSnapshotAsync(scopeId, serviceId, runId, serviceRunQueryPort, ct);
        if (snapshot == null)
        {
            return Results.NotFound(new
            {
                code = "SERVICE_RUN_NOT_FOUND",
                message = BuildScopeServiceRunNotFoundMessage(scopeId, serviceId, runId?.Trim() ?? string.Empty),
            });
        }

        var summary = await BuildScopeRunSummaryFromRegistryAsync(
            scopeId,
            serviceId,
            snapshot,
            workflowExecutionQueryService,
            ct);

        if (snapshot.ImplementationKind != ServiceImplementationKind.Workflow ||
            string.IsNullOrWhiteSpace(snapshot.TargetActorId))
        {
            return Results.NotFound(new
            {
                code = "SERVICE_RUN_AUDIT_NOT_AVAILABLE",
                message = $"Audit detail for run '{snapshot.RunId}' is not available for {snapshot.ImplementationKind} services.",
            });
        }

        var report = await workflowExecutionQueryService.GetActorReportAsync(snapshot.TargetActorId, ct);
        if (report == null)
        {
            return Results.NotFound(new
            {
                code = "SERVICE_RUN_AUDIT_NOT_FOUND",
                message = $"Audit report for run '{snapshot.RunId}' was not found on service '{serviceId}' in scope '{scopeId}'.",
            });
        }

        return Results.Ok(new ScopeServiceRunAuditHttpResponse(summary, report));
    }

    // Registers a stream-invocation run with the durable service-run registry using the
    // actual run id that the implementation pipeline produced (workflow run actor id /
    // draft-run command id / scripting-generated run id). Called once the downstream
    // run id is known so /runs/{runId} resolves the same id the client receives via SSE.
    private static ValueTask RegisterStreamServiceRunAsync(
        IServiceRunRegistrationPort serviceRunRegistrationPort,
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest invocationRequest,
        string scopeId,
        string serviceId,
        string runId,
        string commandId,
        string correlationId,
        string targetActorId,
        CancellationToken ct)
    {
        var record = new ServiceRunRecord
        {
            ScopeId = scopeId,
            ServiceId = serviceId,
            ServiceKey = target.Service.ServiceKey ?? string.Empty,
            RunId = runId,
            CommandId = string.IsNullOrWhiteSpace(commandId) ? runId : commandId,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? runId : correlationId,
            EndpointId = target.Endpoint.EndpointId ?? string.Empty,
            ImplementationKind = target.Artifact.ImplementationKind,
            TargetActorId = string.IsNullOrWhiteSpace(targetActorId)
                ? target.Service.PrimaryActorId ?? string.Empty
                : targetActorId,
            RevisionId = target.Service.RevisionId ?? string.Empty,
            DeploymentId = target.Service.DeploymentId ?? string.Empty,
            Status = ServiceRunStatus.Accepted,
            Identity = invocationRequest.Identity?.Clone(),
        };
        return new ValueTask(serviceRunRegistrationPort.RegisterAsync(record, ct));
    }

    private static async Task<ServiceRunSnapshot?> ResolveServiceRunSnapshotAsync(
        string scopeId,
        string serviceId,
        string runId,
        IServiceRunQueryPort serviceRunQueryPort,
        CancellationToken ct)
    {
        var normalized = runId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var byRun = await serviceRunQueryPort.GetByRunIdAsync(scopeId, serviceId, normalized, ct);
        if (byRun != null)
            return byRun;

        return await serviceRunQueryPort.GetByCommandIdAsync(scopeId, serviceId, normalized, ct);
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
        [FromServices] IServiceRunRegistrationPort serviceRunRegistrationPort,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        [FromServices] ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> gagentDraftRunService,
        [FromServices] IScriptRuntimeCommandPort scriptRuntimeCommandPort,
        [FromServices] IScriptExecutionProjectionPort scriptExecutionProjectionPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var normalizedPrompt = request.Prompt?.Trim() ?? string.Empty;
            var scopedHeaders = await BuildScopedHeadersAsync(scopeId, request.Headers, http, ct);
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
                            InputParts = MapInputParts(request.InputParts),
                            AgentId = target.Service.PrimaryActorId,
                            SessionId = request.SessionId,
                            ScopeId = scopeId,
                            Metadata = scopedHeaders,
                        },
                        chatRunService,
                        ct,
                        onAcceptedHook: (receipt, token) => RegisterStreamServiceRunAsync(
                            serviceRunRegistrationPort,
                            target,
                            invocationRequest,
                            scopeId,
                            serviceId,
                            // For workflow, the SSE RunStarted carries the workflow run actor id as the run identifier;
                            // use the same id so /runs/{runId} resolves to this run after refresh.
                            runId: receipt.ActorId,
                            commandId: receipt.CommandId,
                            correlationId: receipt.CorrelationId,
                            targetActorId: receipt.ActorId,
                            token));
                    break;

                case ServiceImplementationKind.Static:
                    await HandleStaticGAgentChatStreamAsync(
                        http,
                        target,
                        normalizedPrompt,
                        request.ActorId,
                        request.SessionId,
                        scopeId,
                        serviceId,
                        scopedHeaders,
                        request.InputParts,
                        gagentDraftRunService,
                        invocationRequest,
                        serviceRunRegistrationPort,
                        ct);
                    break;

                case ServiceImplementationKind.Scripting:
                    await HandleScriptingServiceChatStreamAsync(
                        http,
                        target,
                        normalizedPrompt,
                        request.SessionId,
                        scopeId,
                        serviceId,
                        scopedHeaders,
                        scriptRuntimeCommandPort,
                        scriptExecutionProjectionPort,
                        invocationRequest,
                        serviceRunRegistrationPort,
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
        string? actorId,
        string? sessionId,
        string scopeId,
        string serviceId,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyList<StreamContentPartHttpRequest>? inputParts,
        ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> interactionService,
        ServiceInvocationRequest invocationRequest,
        IServiceRunRegistrationPort serviceRunRegistrationPort,
        CancellationToken ct)
    {
        var plan = target.Artifact.DeploymentPlan.StaticPlan;
        var resolvedActorId = string.IsNullOrWhiteSpace(actorId)
            ? null
            : actorId.Trim();

        var writer = new AGUISseWriter(http.Response);
        var responseStarted = false;

        async Task EnsureSseStartedAsync(CancellationToken token)
        {
            if (responseStarted)
                return;

            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.StartAsync(token);
            responseStarted = true;
        }

        async ValueTask EmitAsync(AGUIEvent aguiEvent, CancellationToken token)
        {
            await EnsureSseStartedAsync(token);
            await writer.WriteAsync(aguiEvent, token);
        }

        async ValueTask OnAcceptedAsync(GAgentDraftRunAcceptedReceipt receipt, CancellationToken token)
        {
            http.Response.Headers["X-Correlation-Id"] = receipt.CorrelationId;
            // Register the service run with the same id we are about to send to the client
            // so /runs/{runId} resolves immediately on refresh.
            await RegisterStreamServiceRunAsync(
                serviceRunRegistrationPort,
                target,
                invocationRequest,
                scopeId,
                serviceId,
                runId: receipt.CommandId,
                commandId: receipt.CommandId,
                correlationId: receipt.CorrelationId,
                targetActorId: receipt.ActorId,
                token);
            await EnsureSseStartedAsync(token);
            await writer.WriteAsync(
                new AGUIEvent
                {
                    RunStarted = new RunStartedEvent
                    {
                        ThreadId = receipt.ActorId,
                        RunId = receipt.CommandId,
                    },
                },
                token);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            var interaction = await interactionService.ExecuteAsync(
                new GAgentDraftRunCommand(
                    ScopeId: scopeId,
                    ActorTypeName: plan.ActorTypeName,
                    Prompt: prompt,
                    PreferredActorId: resolvedActorId,
                    SessionId: sessionId,
                    Headers: headers,
                    InputParts: MapGAgentDraftRunInputParts(inputParts),
                    UseCorrelationIdAsFallbackSessionId: false),
                EmitAsync,
                OnAcceptedAsync,
                timeoutCts.Token);

            if (!interaction.Succeeded && interaction.Error == GAgentDraftRunStartError.UnknownActorType)
            {
                throw new InvalidOperationException(
                    $"GAgent type '{plan.ActorTypeName}' could not be resolved.");
            }

            if (!interaction.Succeeded && interaction.Error == GAgentDraftRunStartError.ActorTypeMismatch)
            {
                throw new InvalidOperationException(
                    $"Actor '{resolvedActorId}' is not compatible with requested type '{plan.ActorTypeName}'.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await EnsureSseStartedAsync(CancellationToken.None);
            await writer.WriteAsync(
                new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = "GAgent service chat stream timed out.",
                    },
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            var isAuthRequired = ex is NyxIdAuthenticationRequiredException;
            if (!responseStarted)
                throw;

            await writer.WriteAsync(
                new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = isAuthRequired
                            ? "NyxID authentication required. Please sign in."
                            : ex.Message,
                        Code = isAuthRequired ? "authentication_required" : null,
                    },
                },
                CancellationToken.None);
        }
    }

    internal static bool ShouldEmitSyntheticRunFinished(AGUIEvent.EventOneofCase terminalEventCase) =>
        terminalEventCase == AGUIEvent.EventOneofCase.TextMessageEnd;

    private static async Task HandleScriptingServiceChatStreamAsync(
        HttpContext http,
        ServiceInvocationResolvedTarget target,
        string prompt,
        string? sessionId,
        string scopeId,
        string serviceId,
        IReadOnlyDictionary<string, string>? headers,
        IScriptRuntimeCommandPort scriptRuntimeCommandPort,
        IScriptExecutionProjectionPort scriptExecutionProjectionPort,
        ServiceInvocationRequest invocationRequest,
        IServiceRunRegistrationPort serviceRunRegistrationPort,
        CancellationToken ct)
    {
        var actorId = target.Service.PrimaryActorId;
        if (string.IsNullOrWhiteSpace(actorId))
            throw new InvalidOperationException(
                "Script runtime actor is not available. The service may not be activated.");

        var runId = Guid.NewGuid().ToString("N");
        // Register the service run with the same id the SSE RunStarted frame will carry.
        await RegisterStreamServiceRunAsync(
            serviceRunRegistrationPort,
            target,
            invocationRequest,
            scopeId,
            serviceId,
            runId: runId,
            commandId: runId,
            correlationId: runId,
            targetActorId: actorId,
            ct);
        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId ?? string.Empty,
            ScopeId = scopeId,
        };
        CopyHeaders(headers, chatRequest.Metadata);
        var inputPayload = Any.Pack(chatRequest);
        var eventChannel = new EventChannel<EventEnvelope>();
        var projectionLease = await scriptExecutionProjectionPort.EnsureAndAttachAsync(
            token => scriptExecutionProjectionPort.EnsureRunProjectionAsync(actorId, runId, token),
            eventChannel,
            ct);
        if (projectionLease == null)
            throw new InvalidOperationException("Script execution projection pipeline is unavailable.");

        Task? pumpTask = null;

        try
        {
            await scriptRuntimeCommandPort.RunRuntimeAsync(
                actorId,
                runId,
                inputPayload,
                target.Artifact.DeploymentPlan.ScriptingPlan.Revision,
                target.Artifact.DeploymentPlan.ScriptingPlan.DefinitionActorId,
                inputPayload.TypeUrl,
                scopeId,
                ct);

            var writer = new AGUISseWriter(http.Response);
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.StartAsync(ct);
            await writer.WriteAsync(new AGUIEvent
            {
                RunStarted = new RunStartedEvent { ThreadId = actorId, RunId = runId },
            }, ct);

            var tcs = new TaskCompletionSource<AGUIEvent.EventOneofCase>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctr = ct.Register(() => tcs.TrySetCanceled(ct));
            pumpTask = PumpScriptEventsAsync(eventChannel, writer, tcs);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(120_000, CancellationToken.None));
            if (completedTask != tcs.Task)
            {
                await writer.WriteAsync(new AGUIEvent
                {
                    RunError = new RunErrorEvent { Message = "Script service chat stream timed out." },
                }, CancellationToken.None);
                return;
            }

            var terminalEventCase = await tcs.Task;
            if (ShouldEmitSyntheticRunFinished(terminalEventCase))
            {
                await writer.WriteAsync(new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent { ThreadId = actorId, RunId = runId },
                }, CancellationToken.None);
            }
        }
        finally
        {
            await scriptExecutionProjectionPort.DetachReleaseAndDisposeAsync(
                projectionLease,
                eventChannel,
                null,
                CancellationToken.None);

            if (pumpTask != null)
            {
                try
                {
                    await pumpTask;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Client disconnected.
                }
            }
        }
    }

    private static async Task PumpScriptEventsAsync(
        IEventSink<EventEnvelope> eventSink,
        AGUISseWriter writer,
        TaskCompletionSource<AGUIEvent.EventOneofCase> completionSource)
    {
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(completionSource);

        try
        {
            await foreach (var envelope in eventSink.ReadAllAsync(CancellationToken.None))
            {
                var aguiEvent = ScopeGAgentEndpoints.TryMapEnvelopeToAguiEvent(envelope);
                if (aguiEvent is null)
                    continue;

                await writer.WriteAsync(aguiEvent, CancellationToken.None);
                if (aguiEvent.EventCase is AGUIEvent.EventOneofCase.RunFinished
                    or AGUIEvent.EventOneofCase.RunError
                    or AGUIEvent.EventOneofCase.TextMessageEnd)
                {
                    completionSource.TrySetResult(aguiEvent.EventCase);
                }
            }
        }
        catch (Exception ex)
        {
            completionSource.TrySetException(ex);
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
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionArtifactStore artifactStore,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        await HandleInvokeAsyncCore(
            http,
            scopeId,
            serviceId,
            endpointId,
            request,
            appId,
            acceptedResourcePath: null,
            invocationPort,
            catalogReader,
            artifactStore,
            options,
            ct);

    private static async Task<IResult> HandleInvokeAsyncCore(
        HttpContext http,
        string scopeId,
        string serviceId,
        string endpointId,
        InvokeScopeServiceHttpRequest request,
        string? appId,
        string? acceptedResourcePath,
        IServiceInvocationPort invocationPort,
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionArtifactStore artifactStore,
        IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var identity = BuildScopeServiceIdentity(options.Value, scopeId, serviceId, appId);
            var typeUrl = request.PayloadTypeUrl?.Trim() ?? string.Empty;
            var revisionId = request.RevisionId?.Trim() ?? string.Empty;
            var (payload, resolvedRevisionId) = await ResolveInvocationPayloadAsync(
                request,
                typeUrl,
                revisionId,
                identity,
                catalogReader,
                artifactStore,
                ct);

            var receipt = await invocationPort.InvokeAsync(new ServiceInvocationRequest
            {
                Identity = identity,
                EndpointId = endpointId?.Trim() ?? string.Empty,
                CommandId = request.CommandId?.Trim() ?? string.Empty,
                CorrelationId = request.CorrelationId?.Trim() ?? string.Empty,
                RevisionId = resolvedRevisionId,
                Payload = payload,
                Caller = new ServiceInvocationCaller
                {
                    ServiceKey = string.Empty,
                    TenantId = string.Empty,
                    AppId = string.Empty,
                },
            }, ct);
            var locationPath = string.IsNullOrWhiteSpace(acceptedResourcePath)
                ? $"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}"
                : acceptedResourcePath;
            return Results.Accepted(locationPath, receipt);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return CreateScopeInvokeFailureResult(ex);
        }
    }

    private static async Task<(Any Payload, string RevisionId)> ResolveInvocationPayloadAsync(
        InvokeScopeServiceHttpRequest request,
        string typeUrl,
        string requestedRevisionId,
        ServiceIdentity identity,
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionArtifactStore artifactStore,
        CancellationToken ct)
    {
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
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
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
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
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
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
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
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var snapshot = await queryPort.GetBindingsAsync(
            BuildScopeServiceIdentity(options.Value, scopeId, serviceId),
            ct);
        return snapshot == null ? Results.NotFound() : Results.Ok(snapshot);
    }

    private static async Task<IResult> HandleGetEndpointContractAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string endpointId,
        string? appId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return Results.BadRequest(new
            {
                code = "INVALID_ENDPOINT_ID",
                message = "endpointId is required.",
            });
        }

        var resolution = await ResolveScopeServiceAsync(
            http,
            scopeId,
            serviceId,
            lifecycleQueryPort,
            options.Value,
            ct,
            appId);
        if (resolution.Failure != null)
            return resolution.Failure;

        var revisions = await lifecycleQueryPort.GetServiceRevisionsAsync(resolution.Identity!, ct);
        var contract = BuildScopeServiceEndpointContractResponse(
            scopeId,
            serviceId,
            endpointId,
            resolution.Service!,
            revisions);
        if (contract != null)
            return Results.Ok(contract);

        var normalizedEndpointId = NormalizeOptional(endpointId) ?? endpointId.Trim();
        return Results.NotFound(new
        {
            code = "SCOPE_SERVICE_ENDPOINT_CONTRACT_NOT_FOUND",
            message = $"Endpoint '{normalizedEndpointId}' was not found on service '{serviceId}' in scope '{scopeId}'.",
        });
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
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
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
        CancellationToken ct,
        string? appId = null)
    {
        var normalizedRunId = ScopeWorkflowCapabilityOptions.NormalizeRequired(runId, nameof(runId));
        var scopeService = await ResolveScopeServiceAsync(http, scopeId, serviceId, lifecycleQueryPort, options, ct, appId);
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

    private static MemberPublishedServiceHttpResponse BuildMemberPublishedServiceResponse(
        MemberPublishedServiceResolution memberResolution,
        ServiceIdentity identity)
    {
        return new MemberPublishedServiceHttpResponse(
            memberResolution.ScopeId,
            memberResolution.MemberId,
            memberResolution.PublishedServiceId,
            ServiceKeys.Build(identity));
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

    private static ScopeServiceEndpointContractHttpResponse? BuildScopeServiceEndpointContractResponse(
        string scopeId,
        string serviceId,
        string endpointId,
        ServiceCatalogSnapshot service,
        ServiceRevisionCatalogSnapshot? revisions)
    {
        var normalizedEndpointId = ScopeWorkflowCapabilityOptions.NormalizeRequired(endpointId, nameof(endpointId));
        var currentRevision = ResolveCurrentContractRevision(service, revisions, normalizedEndpointId);
        var endpoint = currentRevision?.Endpoints.FirstOrDefault(x =>
                string.Equals(x.EndpointId, normalizedEndpointId, StringComparison.Ordinal))
            ?? service.Endpoints.FirstOrDefault(x =>
                string.Equals(x.EndpointId, normalizedEndpointId, StringComparison.Ordinal));
        if (endpoint == null)
            return null;

        var implementationKind = NormalizeOptional(currentRevision?.ImplementationKind);
        var supportsSse = IsChatEndpoint(endpoint.Kind);
        var streamFrameFormat = ResolveScopeServiceStreamFrameFormat(supportsSse, implementationKind);
        var supportsAguiFrames = string.Equals(streamFrameFormat, StreamFrameFormatAgui, StringComparison.Ordinal);
        var invokePath = supportsSse
            ? BuildScopeServiceStreamInvokePath(scopeId, serviceId, normalizedEndpointId)
            : BuildScopeServiceInvokePath(scopeId, serviceId, normalizedEndpointId);
        var responseContentType = supportsSse
            ? "text/event-stream"
            : "application/json";
        var defaultSmokeInputMode = supportsSse
            ? "prompt"
            : "typed-payload";
        var defaultSmokePrompt = supportsSse
            ? DefaultScopeServiceSmokePrompt
            : null;
        var sampleRequestJson = supportsSse
            ? null
            : BuildTypedInvokeRequestExampleBody(endpoint.RequestTypeUrl, prettyPrinted: true);
        var smokeTestSupported = supportsSse || sampleRequestJson != null;

        return new ScopeServiceEndpointContractHttpResponse(
            ScopeId: scopeId,
            ServiceId: serviceId,
            EndpointId: normalizedEndpointId,
            InvokePath: invokePath,
            Method: "POST",
            RequestContentType: "application/json",
            ResponseContentType: responseContentType,
            RequestTypeUrl: endpoint.RequestTypeUrl,
            ResponseTypeUrl: endpoint.ResponseTypeUrl,
            SupportsSse: supportsSse,
            // This contract currently exposes HTTP POST plus optional SSE streaming only.
            SupportsWebSocket: false,
            SupportsAguiFrames: supportsAguiFrames,
            StreamFrameFormat: streamFrameFormat,
            SmokeTestSupported: smokeTestSupported,
            DefaultSmokeInputMode: defaultSmokeInputMode,
            DefaultSmokePrompt: defaultSmokePrompt,
            SampleRequestJson: sampleRequestJson,
            DeploymentStatus: service.DeploymentStatus,
            RevisionId: currentRevision?.RevisionId
                ?? NormalizeOptional(service.DefaultServingRevisionId)
                ?? NormalizeOptional(service.ActiveServingRevisionId)
                ?? string.Empty,
            CurlExample: smokeTestSupported
                ? BuildScopeServiceCurlExample(invokePath, supportsSse, endpoint.RequestTypeUrl)
                : null,
            FetchExample: smokeTestSupported
                ? BuildScopeServiceFetchExample(invokePath, supportsSse, endpoint.RequestTypeUrl)
                : null);
    }

    // Pure projection helpers were moved to
    // Aevatar.GAgentService.Abstractions.Services.ServiceEndpointContractMath
    // so the legacy scope-default route here and the new member-first
    // Studio route share one source of truth — a fix in one no longer
    // silently rots the other. The thin wrappers keep call-site
    // compatibility for the rest of this file.
    private static string? ResolveScopeServiceStreamFrameFormat(bool supportsSse, string? implementationKind) =>
        ServiceEndpointContractMath.ResolveStreamFrameFormat(supportsSse, implementationKind);

    private static ServiceRevisionSnapshot? ResolveCurrentContractRevision(
        ServiceCatalogSnapshot service,
        ServiceRevisionCatalogSnapshot? revisions,
        string endpointId) =>
        ServiceEndpointContractMath.ResolveCurrentContractRevision(service, revisions, endpointId);

    private static bool IsChatEndpoint(string? endpointKind) =>
        ServiceEndpointContractMath.IsChatEndpoint(endpointKind);

    private static string BuildScopeServiceInvokePath(string scopeId, string serviceId, string endpointId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/invoke/{Uri.EscapeDataString(endpointId)}";

    private static string BuildScopeServiceStreamInvokePath(string scopeId, string serviceId, string endpointId) =>
        $"{BuildScopeServiceInvokePath(scopeId, serviceId, endpointId)}:stream";

    private static string BuildMemberApiPath(string scopeId, string memberId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/members/{Uri.EscapeDataString(memberId)}";

    private static string? BuildTypedInvokeRequestExampleBody(string? requestTypeUrl, bool prettyPrinted) =>
        ServiceEndpointContractMath.BuildTypedInvokeRequestExampleBody(requestTypeUrl, prettyPrinted);

    private static string BuildBase64PayloadPlaceholder(string requestTypeUrl) =>
        ServiceEndpointContractMath.BuildBase64PayloadPlaceholder(requestTypeUrl);

    private static string BuildScopeServiceCurlExample(
        string invokePath,
        bool supportsSse,
        string? requestTypeUrl)
    {
        if (supportsSse)
        {
            var requestBody = JsonSerializer.Serialize(
                new { prompt = DefaultScopeServiceSmokePrompt });
            return $"""
curl -N -X POST \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -H "Authorization: Bearer <token>" \
  "{invokePath}" \
  -d '{requestBody}'
""";
        }

        var typedBody = BuildTypedInvokeRequestExampleBody(requestTypeUrl, prettyPrinted: false) ?? "{}";
        return $"""
curl -X POST \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <token>" \
  "{invokePath}" \
  -d '{typedBody}'
""";
    }

    private static string BuildScopeServiceFetchExample(
        string invokePath,
        bool supportsSse,
        string? requestTypeUrl)
    {
        if (supportsSse)
        {
            return $$"""
const response = await fetch("{{invokePath}}", {
  method: "POST",
  headers: {
    "Content-Type": "application/json",
    "Accept": "text/event-stream",
    "Authorization": "Bearer <token>",
  },
  body: JSON.stringify({
    prompt: "{{DefaultScopeServiceSmokePrompt}}",
  }),
});

// Consume response.body as an SSE stream.
""";
        }

        var normalizedRequestTypeUrl = NormalizeOptional(requestTypeUrl) ?? "<type-url>";
        var payloadBase64 = BuildBase64PayloadPlaceholder(normalizedRequestTypeUrl);
        return $$"""
const response = await fetch("{{invokePath}}", {
  method: "POST",
  headers: {
    "Content-Type": "application/json",
    "Authorization": "Bearer <token>",
  },
  body: JSON.stringify({
    payloadTypeUrl: "{{normalizedRequestTypeUrl}}",
    payloadBase64: "{{payloadBase64}}",
  }),
});
""";
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
                    revision.Implementation?.Static?.ActorTypeName ?? string.Empty);
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
            snapshot?.LastError ?? string.Empty,
            ServiceImplementationKind.Workflow.ToString(),
            ServiceRunStatus.Accepted.ToString(),
            string.Empty,
            string.Empty,
            string.Empty,
            binding.ActorId,
            binding.CreatedAt);
    }

    private static async Task<ScopeServiceRunSummaryHttpResponse> BuildScopeRunSummaryFromRegistryAsync(
        string scopeId,
        string serviceId,
        ServiceRunSnapshot snapshot,
        IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        CancellationToken ct)
    {
        var workflowSnapshot = snapshot.ImplementationKind == ServiceImplementationKind.Workflow &&
                               !string.IsNullOrWhiteSpace(snapshot.TargetActorId)
            ? await workflowExecutionQueryService.GetActorSnapshotAsync(snapshot.TargetActorId, ct)
            : null;

        return new ScopeServiceRunSummaryHttpResponse(
            scopeId,
            serviceId,
            snapshot.RunId,
            // ActorId stays the controllable target so existing resume/signal/stop
            // round-trips keep working; the registry actor is internal infra.
            snapshot.TargetActorId,
            string.Empty,
            snapshot.RevisionId,
            snapshot.DeploymentId,
            workflowSnapshot?.WorkflowName ?? string.Empty,
            workflowSnapshot?.CompletionStatus ?? WorkflowRunCompletionStatus.Unknown,
            workflowSnapshot?.StateVersion ?? snapshot.StateVersion,
            workflowSnapshot?.LastEventId ?? snapshot.LastEventId,
            workflowSnapshot?.LastUpdatedAt ?? snapshot.UpdatedAt,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            workflowSnapshot?.LastSuccess,
            workflowSnapshot?.TotalSteps ?? 0,
            workflowSnapshot?.CompletedSteps ?? 0,
            workflowSnapshot?.RoleReplyCount ?? 0,
            workflowSnapshot?.LastOutput ?? string.Empty,
            workflowSnapshot?.LastError ?? string.Empty,
            snapshot.ImplementationKind.ToString(),
            snapshot.Status.ToString(),
            snapshot.CommandId,
            snapshot.CorrelationId,
            snapshot.EndpointId,
            snapshot.TargetActorId,
            snapshot.CreatedAt);
    }

    private static MemberScopeServiceRunSummaryHttpResponse BuildMemberRunSummaryResponse(
        MemberPublishedServiceResolution memberResolution,
        ScopeServiceRunSummaryHttpResponse summary)
    {
        return new MemberScopeServiceRunSummaryHttpResponse(
            summary.ScopeId,
            memberResolution.MemberId,
            memberResolution.PublishedServiceId,
            summary.RunId,
            summary.ActorId,
            summary.DefinitionActorId,
            summary.RevisionId,
            summary.DeploymentId,
            summary.WorkflowName,
            summary.CompletionStatus,
            summary.StateVersion,
            summary.LastEventId,
            summary.LastUpdatedAt,
            summary.BoundAt,
            summary.BindingUpdatedAt,
            summary.LastSuccess,
            summary.TotalSteps,
            summary.CompletedSteps,
            summary.RoleReplyCount,
            summary.LastOutput,
            summary.LastError);
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

    private static async Task<Dictionary<string, string>> BuildScopedHeadersAsync(
        string scopeId,
        IReadOnlyDictionary<string, string>? headers,
        HttpContext? http = null,
        CancellationToken cancellationToken = default)
    {
        var scopedHeaders = headers == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        scopedHeaders.Remove("scope_id");
        scopedHeaders.Remove(WorkflowRunCommandMetadataKeys.ScopeId);
        InjectBearerToken(http, scopedHeaders);
        if (http != null)
        {
            var userConfigStore = http.RequestServices.GetService<IUserConfigQueryPort>();
            if (userConfigStore != null)
            {
                try
                {
                    var userConfig = await userConfigStore.GetAsync(cancellationToken);
                    if (!scopedHeaders.ContainsKey(LLMRequestMetadataKeys.ModelOverride) &&
                        !string.IsNullOrWhiteSpace(userConfig.DefaultModel))
                        scopedHeaders[LLMRequestMetadataKeys.ModelOverride] = userConfig.DefaultModel.Trim();
                    if (!scopedHeaders.ContainsKey(LLMRequestMetadataKeys.NyxIdRoutePreference) &&
                        !string.IsNullOrWhiteSpace(userConfig.PreferredLlmRoute))
                        scopedHeaders[LLMRequestMetadataKeys.NyxIdRoutePreference] = userConfig.PreferredLlmRoute.Trim();
                }
                catch
                {
                    // Best-effort; fall back to provider defaults if config unavailable.
                }
            }
        }
        return scopedHeaders;
    }

    private static void InjectBearerToken(HttpContext? http, Dictionary<string, string> metadata)
    {
        if (http == null)
            return;
        var auth = http.Request.Headers.Authorization.FirstOrDefault();
        if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearerToken = auth["Bearer ".Length..].Trim();
            metadata["nyxid.access_token"] = bearerToken;
            metadata[ConnectorRequest.HttpAuthorizationMetadataKey] = $"Bearer {bearerToken}";
        }
    }

    private static void CopyHeaders(
        IReadOnlyDictionary<string, string>? source,
        IDictionary<string, string> target)
    {
        if (source == null)
            return;

        foreach (var (key, value) in source)
        {
            target[key] = value;
        }
    }

    private static IReadOnlyList<ChatInputContentPart>? MapInputParts(
        IReadOnlyList<StreamContentPartHttpRequest>? parts)
    {
        if (parts is not { Count: > 0 })
            return null;

        return parts
            .Where(p => p != null)
            .Select(p => new ChatInputContentPart
            {
                Type = p.Type,
                Text = p.Text,
                DataBase64 = p.DataBase64,
                MediaType = p.MediaType,
                Uri = p.Uri,
                Name = p.Name,
            }).ToList();
    }

    private static IReadOnlyList<GAgentDraftRunInputPart>? MapGAgentDraftRunInputParts(
        IReadOnlyList<StreamContentPartHttpRequest>? parts)
    {
        if (parts is not { Count: > 0 })
            return null;

        return parts
            .Where(p => p != null)
            .Select(p => new GAgentDraftRunInputPart
            {
                Kind = p.Type?.ToLowerInvariant() switch
                {
                    "image" => GAgentDraftRunInputPartKind.Image,
                    "audio" => GAgentDraftRunInputPartKind.Audio,
                    "video" => GAgentDraftRunInputPartKind.Video,
                    "text" => GAgentDraftRunInputPartKind.Text,
                    _ => GAgentDraftRunInputPartKind.Unspecified,
                },
                Text = p.Text,
                DataBase64 = p.DataBase64,
                MediaType = p.MediaType,
                Uri = p.Uri,
                Name = p.Name,
            }).ToList();
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

    public sealed record InvokeScopeServiceHttpRequest(
        string? CommandId,
        string? CorrelationId,
        string? PayloadTypeUrl,
        string? PayloadBase64,
        string? RevisionId = null,
        string? PayloadJson = null);

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
        IReadOnlyList<ServiceEndpoints.ServiceEndpointHttpRequest>? Endpoints);

    public sealed record StreamScopeServiceHttpRequest(
        string? Prompt,
        string? ActorId = null,
        string? SessionId = null,
        Dictionary<string, string>? Headers = null,
        string? RevisionId = null,
        IReadOnlyList<StreamContentPartHttpRequest>? InputParts = null);

    public sealed record StreamContentPartHttpRequest(
        string Type,
        string? Text = null,
        string? DataBase64 = null,
        string? MediaType = null,
        string? Uri = null,
        string? Name = null);

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

    public sealed record MemberPublishedServiceHttpResponse(
        string ScopeId,
        string MemberId,
        string PublishedServiceId,
        string PublishedServiceKey);

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
        string StaticActorTypeName = "");

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

    public sealed record ScopeServiceEndpointContractHttpResponse(
        string ScopeId,
        string ServiceId,
        string EndpointId,
        string InvokePath,
        string Method,
        string RequestContentType,
        string ResponseContentType,
        string RequestTypeUrl,
        string ResponseTypeUrl,
        bool SupportsSse,
        bool SupportsWebSocket,
        bool SupportsAguiFrames,
        string? StreamFrameFormat,
        bool SmokeTestSupported,
        string DefaultSmokeInputMode,
        string? DefaultSmokePrompt,
        string? SampleRequestJson,
        string DeploymentStatus,
        string RevisionId,
        string? CurlExample = null,
        string? FetchExample = null);

    public sealed record ScopeServiceRunCatalogHttpResponse(
        string ScopeId,
        string ServiceId,
        string ServiceKey,
        string DisplayName,
        IReadOnlyList<ScopeServiceRunSummaryHttpResponse> Runs);

    public sealed record MemberScopeServiceRunCatalogHttpResponse(
        string ScopeId,
        string MemberId,
        string PublishedServiceId,
        string PublishedServiceKey,
        string DisplayName,
        IReadOnlyList<MemberScopeServiceRunSummaryHttpResponse> Runs);

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
        string LastError,
        string ImplementationKind,
        string Status,
        string CommandId,
        string CorrelationId,
        string EndpointId,
        string TargetActorId,
        DateTimeOffset? CreatedAt = null);

    public sealed record MemberScopeServiceRunSummaryHttpResponse(
        string ScopeId,
        string MemberId,
        string PublishedServiceId,
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

    public sealed record MemberScopeServiceRunAuditHttpResponse(
        MemberScopeServiceRunSummaryHttpResponse Summary,
        WorkflowRunReport Audit);
}
