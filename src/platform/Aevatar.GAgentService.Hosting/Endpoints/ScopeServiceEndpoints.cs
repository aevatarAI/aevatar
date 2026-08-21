using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AGUI.Contracts;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Capabilities;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Serialization;
using Aevatar.GAgentService.Hosting.Sse;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkflowRunOrigins = Aevatar.Workflow.Abstractions.WorkflowRunOrigins;
using WorkflowSagaStatus = Aevatar.Workflow.Abstractions.WorkflowSagaStatus;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ScopeServiceEndpoints
{
    private const string DefaultScopeServiceSmokePrompt = "Hello from Studio Bind.";
    private const string StreamFrameFormatWorkflow = "workflow-run-event";
    private const string StreamFrameFormatAgui = "agui";
    private const string LegacyConnectorHttpAuthorizationBlockedKey = "connector.http.authorization";
    private static readonly JsonSerializerOptions PrettyJsonSerializerOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions ScopeRequestJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapScopeServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = ScopeEndpointRouteGroups.MapScopeGroup(app).WithTags("ScopeServices");
        group.MapPost("/{scopeId}/workflow/draft-run", HandleDraftRunAsync)
            .WithScopeServiceAudit("scope.workflow.draft-run", "scope-workflow-draft", "scopeId");
        group.MapPut("/{scopeId}/binding", HandleUpsertBindingAsync)
            .WithScopeServiceAudit("scope.binding.upsert", "scope-binding", "scopeId");
        group.MapGet("/{scopeId}/binding", HandleGetBindingAsync);
        group.MapGet("/{scopeId}/members/{memberId}/published-service", HandleGetMemberPublishedServiceAsync);
        group.MapPost("/{scopeId}/binding/revisions/{revisionId}:activate", HandleActivateBindingRevisionAsync)
            .WithScopeServiceAudit("scope.binding-revision.activate", "scope-binding-revision", "scopeId", "revisionId");
        group.MapGet("/{scopeId}/revisions", HandleGetDefaultServiceRevisionsAsync);
        group.MapGet("/{scopeId}/revisions/{revisionId}", HandleGetDefaultServiceRevisionAsync);
        group.MapPost("/{scopeId}/binding/revisions/{revisionId}:retire", HandleRetireBindingRevisionAsync)
            .WithScopeServiceAudit("scope.binding-revision.retire", "scope-binding-revision", "scopeId", "revisionId");
        group.MapPost("/{scopeId}/invoke/chat:stream", HandleInvokeDefaultChatStreamAsync)
            .WithScopeServiceAudit("scope.default-service.invoke-chat-stream", "scope-service-invocation", "scopeId");
        group.MapPost("/{scopeId}/invoke/{endpointId}", HandleInvokeDefaultAsync)
            .WithScopeServiceAudit("scope.default-service.invoke", "scope-service-invocation", "scopeId", "endpointId");
        group.MapPost("/{scopeId}/members/{memberId}/invoke/{endpointId}:stream", HandleInvokeMemberStreamAsync)
            .WithScopeServiceAudit("scope.member.invoke-stream", "scope-member-invocation", "scopeId", "memberId", "endpointId");
        group.MapPost("/{scopeId}/members/{memberId}/invoke/{endpointId}", HandleInvokeMemberAsync)
            .WithScopeServiceAudit("scope.member.invoke", "scope-member-invocation", "scopeId", "memberId", "endpointId");
        group.MapPost("/{scopeId}/teams/{teamId}/invoke/{endpointId}:stream", HandleInvokeTeamStreamAsync)
            .WithScopeServiceAudit("scope.team.invoke-stream", "scope-team-invocation", "scopeId", "teamId", "endpointId");
        group.MapPost("/{scopeId}/teams/{teamId}/invoke/{endpointId}", HandleInvokeTeamAsync)
            .WithScopeServiceAudit("scope.team.invoke", "scope-team-invocation", "scopeId", "teamId", "endpointId");
        group.MapGet("/{scopeId}/runs", HandleListDefaultRunsAsync);
        group.MapGet("/{scopeId}/runs/{runId}", HandleGetDefaultRunAsync);
        group.MapGet("/{scopeId}/members/{memberId}/runs", HandleListMemberRunsAsync);
        group.MapGet("/{scopeId}/members/{memberId}/runs/{runId}", HandleGetMemberRunAsync);
        group.MapGet("/{scopeId}/members/{memberId}/runs/{runId}/audit", HandleGetMemberRunAuditAsync);
        group.MapPost("/{scopeId}/members/{memberId}/runs/{runId}:resume", HandleResumeMemberRunAsync)
            .WithScopeServiceAudit("scope.member-run.resume", "workflow-run", "scopeId", "memberId", "runId");
        group.MapPost("/{scopeId}/members/{memberId}/runs/{runId}:signal", HandleSignalMemberRunAsync)
            .WithScopeServiceAudit("scope.member-run.signal", "workflow-run", "scopeId", "memberId", "runId");
        group.MapPost("/{scopeId}/members/{memberId}/runs/{runId}:stop", HandleStopMemberRunAsync)
            .WithScopeServiceAudit("scope.member-run.stop", "workflow-run", "scopeId", "memberId", "runId");
        group.MapPost("/{scopeId}/members/{memberId}/runs/{runId}:retry-compensation", HandleRetryCompensationMemberRunAsync)
            .WithScopeServiceAudit("scope.member-run.retry-compensation", "workflow-run", "scopeId", "memberId", "runId");
        group.MapGet("/{scopeId}/runs/{runId}/audit", HandleGetDefaultRunAuditAsync);
        group.MapPost("/{scopeId}/runs/{runId}:resume", HandleResumeDefaultRunAsync)
            .WithScopeServiceAudit("scope.default-run.resume", "workflow-run", "scopeId", "runId");
        group.MapPost("/{scopeId}/runs/{runId}:signal", HandleSignalDefaultRunAsync)
            .WithScopeServiceAudit("scope.default-run.signal", "workflow-run", "scopeId", "runId");
        group.MapPost("/{scopeId}/runs/{runId}:stop", HandleStopDefaultRunAsync)
            .WithScopeServiceAudit("scope.default-run.stop", "workflow-run", "scopeId", "runId");
        group.MapPost("/{scopeId}/runs/{runId}:retry-compensation", HandleRetryCompensationDefaultRunAsync)
            .WithScopeServiceAudit("scope.default-run.retry-compensation", "workflow-run", "scopeId", "runId");
        group.MapGet("/{scopeId}/services", HandleListScopeServicesAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/invoke/{endpointId}:stream", HandleInvokeStreamAsync)
            .WithScopeServiceAudit("scope.service.invoke-stream", "scope-service-invocation", "scopeId", "serviceId", "endpointId");
        group.MapPost("/{scopeId}/services/{serviceId}/invoke/{endpointId}", HandleInvokeAsync)
            .WithScopeServiceAudit("scope.service.invoke", "scope-service-invocation", "scopeId", "serviceId", "endpointId");
        group.MapGet("/{scopeId}/services/{serviceId}/revisions", HandleGetServiceRevisionsAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/revisions/{revisionId}", HandleGetServiceRevisionAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/revisions/{revisionId}:retire", HandleRetireServiceRevisionAsync)
            .WithScopeServiceAudit("scope.service-revision.retire", "scope-service-revision", "scopeId", "serviceId", "revisionId");
        group.MapGet("/{scopeId}/services/{serviceId}/runs", HandleListRunsAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/runs/{runId}", HandleGetRunAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/runs/{runId}/audit", HandleGetRunAuditAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/runs/{runId}:resume", HandleResumeRunAsync)
            .WithScopeServiceAudit("scope.service-run.resume", "workflow-run", "scopeId", "serviceId", "runId");
        group.MapPost("/{scopeId}/services/{serviceId}/runs/{runId}:signal", HandleSignalRunAsync)
            .WithScopeServiceAudit("scope.service-run.signal", "workflow-run", "scopeId", "serviceId", "runId");
        group.MapPost("/{scopeId}/services/{serviceId}/runs/{runId}:stop", HandleStopRunAsync)
            .WithScopeServiceAudit("scope.service-run.stop", "workflow-run", "scopeId", "serviceId", "runId");
        group.MapPost("/{scopeId}/services/{serviceId}/runs/{runId}:retry-compensation", HandleRetryCompensationRunAsync)
            .WithScopeServiceAudit("scope.service-run.retry-compensation", "workflow-run", "scopeId", "serviceId", "runId");
        group.MapPost("/{scopeId}/services/{serviceId}/bindings", HandleCreateBindingAsync)
            .WithScopeServiceAudit("scope.service-binding.create", "scope-service-binding", "scopeId", "serviceId");
        group.MapPut("/{scopeId}/services/{serviceId}/bindings/{bindingId}", HandleUpdateBindingAsync)
            .WithScopeServiceAudit("scope.service-binding.update", "scope-service-binding", "scopeId", "serviceId", "bindingId");
        group.MapPost("/{scopeId}/services/{serviceId}/bindings/{bindingId}:retire", HandleRetireBindingAsync)
            .WithScopeServiceAudit("scope.service-binding.retire", "scope-service-binding", "scopeId", "serviceId", "bindingId");
        group.MapGet("/{scopeId}/services/{serviceId}/bindings", HandleGetBindingsAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/endpoints/{endpointId}/contract", HandleGetEndpointContractAsync);
        return app;
    }

    private static RouteHandlerBuilder WithScopeServiceAudit(
        this RouteHandlerBuilder builder,
        string operationName,
        string targetKind,
        params string[] routeValueNames)
    {
        return builder.WithEndpointAudit(
            operationName,
            AuditSensitivityLevel.Confidential,
            targetKind,
            EndpointAuditTargetResolvers.FromRouteValues(targetKind, routeValueNames),
            EndpointAuditSanitizers.WithRouteValues(routeValueNames));
    }

    private static async Task HandleDraftRunAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IWorkflowChatRunInteractionPort chatRunService,
        [FromServices] WorkflowMultipartFileInputParser multipartFileInputParser,
        [FromServices] IFileArtifactIngressPort workflowFileIngressPort,
        CancellationToken ct)
    {
        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var requestInput = await ParseScopeDraftRunRequestAsync(
                http,
                multipartFileInputParser,
                workflowFileIngressPort,
                scopeId,
                ct);
            if (requestInput.Failure != null)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    requestInput.Failure.Value.StatusCode,
                    requestInput.Failure.Value.Code,
                    requestInput.Failure.Value.Message,
                    ct);
                return;
            }

            var request = requestInput.Request!;
            if (request.WorkflowYamls == null || request.WorkflowYamls.Count == 0)
                throw new InvalidOperationException("workflowYamls is required.");

            var scopedHeaders = BuildScopedHeaders(request.Headers);
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

            var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct);
            if (!callerCredential.Succeeded)
            {
                var (statusCode, code, message) = ScopeWorkflowEndpoints.MapRunStartError(callerCredential.Error);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
                return;
            }

            var normalizedPrompt = request.Prompt?.Trim() ?? string.Empty;
            var llmControl = await BuildScopedLlmControlAsync(http, ct);
            var chatInput = new ChatInput
            {
                Prompt = normalizedPrompt,
                InputParts = requestInput.InputParts,
                WorkflowYamls = request.WorkflowYamls,
                SessionId = request.SessionId,
                ScopeId = scopeId,
                Metadata = scopedHeaders,
                Headers = scopedHeaders,
                LlmControl = ToChatLlmControlInput(llmControl),
            };

            if (eventFormat == ScopeWorkflowEndpoints.ScopeWorkflowStreamEventFormat.Agui)
            {
                var normalizedRequest = await WorkflowCapabilityEndpoints.NormalizeChatInputAsync(
                    chatInput,
                    workflowFileIngressPort,
                    trustedCallerCredential: callerCredential.Credential,
                    cancellationToken: ct,
                    trustedScopeId: scopeId,
                    trustedNyxIdCredentialSelection: callerCredential.NyxIdCredentialSelection);
                if (!normalizedRequest.Succeeded)
                {
                    var (statusCode, code, message) = ScopeWorkflowEndpoints.MapRunStartError(normalizedRequest.Error);
                    await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
                    return;
                }

                await ScopeWorkflowEndpoints.HandleAguiStreamAsync(
                    http,
                    normalizedRequest.Request!,
                    chatRunService,
                    ct);
                return;
            }

            await WorkflowCapabilityEndpoints.HandleChat(
                http,
                chatInput,
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


    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static async ValueTask<ScopeDraftRunRequestInput> ParseScopeDraftRunRequestAsync(
        HttpContext http,
        WorkflowMultipartFileInputParser multipartFileInputParser,
        IFileArtifactIngressPort workflowFileIngressPort,
        string scopeId,
        CancellationToken ct)
    {
        if (WorkflowMultipartFileInputParser.IsMultipartForm(http.Request.ContentType))
        {
            var multipartResult = await multipartFileInputParser.ParseAsync(http, ct);
            if (!multipartResult.Succeeded)
                return ScopeDraftRunRequestInput.Failed(ToScopeDraftRunRequestError(multipartResult.Error!.Value));

            var request = ParseScopeDraftRunPayload(multipartResult.RawPayloadJson);
            if (request == null)
                return ScopeDraftRunRequestInput.Failed(ScopeDraftRunRequestParseError.InvalidRequest);

            var inputParts = MapInputParts(request.InputParts);
            if (multipartResult.Form is { HasFiles: true } form)
            {
                try
                {
                    var uploadedParts = await IngestMultipartInputPartsAsync(
                        form,
                        workflowFileIngressPort,
                        scopeId,
                        ct);
                    inputParts = AppendInputParts(inputParts, uploadedParts);
                }
                catch (InvalidOperationException)
                {
                    return ScopeDraftRunRequestInput.Failed(ScopeDraftRunRequestParseError.InvalidFileInput);
                }
            }

            return ScopeDraftRunRequestInput.Success(request, inputParts);
        }

        if (!IsJsonContentType(http.Request.ContentType))
            return ScopeDraftRunRequestInput.Failed(ScopeDraftRunRequestParseError.UnsupportedMediaType);

        ScopeDraftRunHttpRequest? parsed;
        try
        {
            parsed = await JsonSerializer.DeserializeAsync<ScopeDraftRunHttpRequest>(
                http.Request.Body,
                ScopeRequestJsonOptions,
                ct);
        }
        catch (JsonException)
        {
            return ScopeDraftRunRequestInput.Failed(ScopeDraftRunRequestParseError.InvalidRequest);
        }

        return parsed == null
            ? ScopeDraftRunRequestInput.Failed(ScopeDraftRunRequestParseError.InvalidRequest)
            : ScopeDraftRunRequestInput.Success(parsed, MapInputParts(parsed.InputParts));
    }

    private static ScopeDraftRunHttpRequest? ParseScopeDraftRunPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ScopeDraftRunHttpRequest>(payload, ScopeRequestJsonOptions);
        }
        catch (JsonException)
        {
            return null;
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

            if (request.GAgent?.HasLegacyActorTypeName == true)
            {
                return Results.BadRequest(new
                {
                    code = "LEGACY_ACTOR_TYPE_NAME_REJECTED",
                    message = "gagent.actorTypeName is not accepted. Use gagent.agentKind.",
                });
            }

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
                            request.GAgent.AgentKind ?? string.Empty,
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
                    request.ServiceId,
                    request.ExposureDesired)
                {
                    CapabilityAdmission = await WorkflowCapabilityAdmissionHttpContext.CreateAsync(
                        http,
                        ct: ct),
                },
                ct);
            return Results.Ok(result);
        }
        catch (WorkflowCallerCredentialSelectionException)
        {
            return Results.BadRequest(new
            {
                code = WorkflowCallerCredentialSelectionException.ErrorCode,
                message = WorkflowCallerCredentialSelectionException.SafeMessage,
            });
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

    private static async Task<IResult> HandleListScopeServicesAsync(
        HttpContext http,
        string scopeId,
        string? appId,
        int? take,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceInvocationCatalogQueryReader invocationCatalogQueryReader,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var resolvedAppId = NormalizeOptional(appId)
            ?? ScopeWorkflowCapabilityOptions.NormalizeRequired(
                options.Value.ServiceAppId,
                nameof(ScopeWorkflowCapabilityOptions.ServiceAppId));
        var resolvedNamespace = ScopeWorkflowCapabilityOptions.NormalizeRequired(
            options.Value.ServiceNamespace,
            nameof(ScopeWorkflowCapabilityOptions.ServiceNamespace));

        var services = await lifecycleQueryPort.ListServicesAsync(
            normalizedScopeId,
            resolvedAppId,
            resolvedNamespace,
            take.GetValueOrDefault(200),
            ct);

        return Results.Ok(await JoinScopeInvokeReadinessAsync(services, invocationCatalogQueryReader, ct));
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
            if (TryCreateMemberRouteAccessDeniedResult(http, scopeId, memberId, out var denied))
                return denied;

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

            await commandPort.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
            {
                Identity = identity.Clone(),
                RevisionId = normalizedRevisionId,
                ExpectedArtifactHash = revision.ArtifactHash,
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
        [FromServices] ServiceInvocationResolutionService resolutionService,
        [FromServices] ServiceInvokeReadinessErrorMapper readinessErrorMapper,
        [FromServices] IInvokeAdmissionAuthorizer admissionAuthorizer,
        [FromServices] IServiceRunRegistrationPort serviceRunRegistrationPort,
        [FromServices] IWorkflowChatRunInteractionPort chatRunService,
        [FromServices] WorkflowMultipartFileInputParser multipartFileInputParser,
        [FromServices] IFileArtifactIngressPort workflowFileIngressPort,
        [FromServices] ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>? scriptServiceRunService,
        [FromServices] IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        // Refactor (iter39/cluster-039-scope-service-host-orchestration):
        //   Old pattern: ScopeServiceEndpoints.HandleInvokeDefaultChatStreamAsync 在 unbound default service 情况下 launch Host-inline DefaultChatWorkflowYaml 作为 hidden fallback,把 Host 当成 business orchestrator。
        //   New principle: Host endpoint 仅做 routing + bound service stream;unbound case 返回 explicit error;stream registration / static orchestration 归 Application owner。
        var serviceId = ResolveDefaultScopeServiceId(options.Value);
        await HandleInvokeStreamCoreAsync(
            http,
            scopeId,
            serviceId,
            "chat",
            multipartFileInputParser,
            null,
            false,
            resolutionService,
            readinessErrorMapper,
            admissionAuthorizer,
            serviceRunRegistrationPort,
            chatRunService,
            workflowFileIngressPort,
            scriptServiceRunService,
            staticGAgentStreamInvocationPort,
            options,
            ct);
    }

    private static Task<IResult> HandleInvokeDefaultAsync(
        HttpContext http,
        string scopeId,
        string endpointId,
        InvokeScopeServiceHttpRequest request,
        [FromServices] IServiceInvocationPort invocationPort,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        [FromServices] ServiceInvokeReadinessErrorMapper readinessErrorMapper,
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
            revisionCatalogReader,
            readinessErrorMapper,
            options,
            ct);

    private static async Task HandleInvokeMemberStreamAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string endpointId,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] ServiceInvocationResolutionService resolutionService,
        [FromServices] ServiceInvokeReadinessErrorMapper readinessErrorMapper,
        [FromServices] IInvokeAdmissionAuthorizer admissionAuthorizer,
        [FromServices] IServiceRunRegistrationPort serviceRunRegistrationPort,
        [FromServices] IWorkflowChatRunInteractionPort chatRunService,
        [FromServices] WorkflowMultipartFileInputParser multipartFileInputParser,
        [FromServices] IFileArtifactIngressPort workflowFileIngressPort,
        [FromServices] ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>? scriptServiceRunService,
        [FromServices] IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (await TryWriteMemberRouteAccessDeniedAsync(http, scopeId, memberId, ct))
                return;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            await HandleInvokeStreamCoreAsync(
                http,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                endpointId,
                multipartFileInputParser,
                null,
                memberResolution.IsMemberAuthorityBacked,
                resolutionService,
                readinessErrorMapper,
                admissionAuthorizer,
                serviceRunRegistrationPort,
                chatRunService,
                workflowFileIngressPort,
                scriptServiceRunService,
                staticGAgentStreamInvocationPort,
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
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        [FromServices] ServiceInvokeReadinessErrorMapper readinessErrorMapper,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (TryCreateMemberRouteAccessDeniedResult(http, scopeId, memberId, out var denied))
                return denied;

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
                null,
                invocationPort,
                catalogReader,
                revisionCatalogReader,
                readinessErrorMapper,
                options,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return CreateScopeInvokeFailureResult(ex);
        }
    }

    private static async Task HandleInvokeTeamStreamAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string endpointId,
        [FromServices] ITeamEntryMemberResolver teamEntryMemberResolver,
        [FromServices] ServiceInvocationResolutionService resolutionService,
        [FromServices] ServiceInvokeReadinessErrorMapper readinessErrorMapper,
        [FromServices] IInvokeAdmissionAuthorizer admissionAuthorizer,
        [FromServices] IServiceRunRegistrationPort serviceRunRegistrationPort,
        [FromServices] IWorkflowChatRunInteractionPort chatRunService,
        [FromServices] WorkflowMultipartFileInputParser multipartFileInputParser,
        [FromServices] IFileArtifactIngressPort workflowFileIngressPort,
        [FromServices] ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>? scriptServiceRunService,
        [FromServices] IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var teamResolution = await teamEntryMemberResolver.ResolveAsync(scopeId, teamId, endpointId, ct);
            await HandleInvokeStreamCoreAsync(
                http,
                teamResolution.ScopeId,
                teamResolution.PublishedServiceId,
                endpointId,
                multipartFileInputParser,
                null,
                false,
                resolutionService,
                readinessErrorMapper,
                admissionAuthorizer,
                serviceRunRegistrationPort,
                chatRunService,
                workflowFileIngressPort,
                scriptServiceRunService,
                staticGAgentStreamInvocationPort,
                options,
                ct);
        }
        catch (TeamEntryMemberResolutionException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                ResolveTeamEntryHttpStatusCode(ex.Code),
                ex.Code,
                ex.Message,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_TEAM_SERVICE_STREAM_REQUEST",
                ex.Message,
                ct);
        }
    }

    private static async Task<IResult> HandleInvokeTeamAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        string endpointId,
        InvokeScopeServiceHttpRequest request,
        [FromServices] ITeamEntryMemberResolver teamEntryMemberResolver,
        [FromServices] IServiceInvocationPort invocationPort,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        [FromServices] ServiceInvokeReadinessErrorMapper readinessErrorMapper,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var teamResolution = await teamEntryMemberResolver.ResolveAsync(scopeId, teamId, endpointId, ct);
            return await HandleInvokeAsyncCore(
                http,
                teamResolution.ScopeId,
                teamResolution.PublishedServiceId,
                endpointId,
                request,
                null,
                BuildScopeServiceRunBasePath(teamResolution.ScopeId, teamResolution.PublishedServiceId, teamResolution.EntryMemberId),
                invocationPort,
                catalogReader,
                revisionCatalogReader,
                readinessErrorMapper,
                options,
                ct);
        }
        catch (TeamEntryMemberResolutionException ex)
        {
            return CreateTeamEntryFailureResult(ex);
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
        string? scheduleId,
        string? status,
        string? updatedFrom,
        string? updatedTo,
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
            scheduleId,
            status,
            updatedFrom,
            updatedTo,
            lifecycleQueryPort,
            serviceRunQueryPort,
            workflowExecutionQueryService,
            options,
            ct);

    private static Task<IResult> HandleGetDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
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
            lifecycleQueryPort,
            serviceRunQueryPort,
            workflowExecutionQueryService,
            options,
            ct);

    private static Task<IResult> HandleGetDefaultRunAuditAsync(
        HttpContext http,
        string scopeId,
        string runId,
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
            lifecycleQueryPort,
            serviceRunQueryPort,
            workflowExecutionQueryService,
            options,
            ct);

    private static async Task<IResult> HandleResumeDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        ResumeScopeServiceRunHttpRequest request,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        CancellationToken ct)
    {
        var resolution = await ResolveScopedWorkflowRunAsync(
            http,
            scopeId,
            runId,
            request.ActorId,
            workflowRunBindingReader,
            ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        if (TryCreateInvalidToolApprovalResumeRequest(request, out var invalidRequest))
            return invalidRequest;

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
                ToolApproval = request.ToolApproval == null
                    ? null
                    : new WorkflowToolApprovalResumeInput
                    {
                        ExecutionId = request.ToolApproval.ExecutionId ?? string.Empty,
                        ToolCallId = request.ToolApproval.ToolCallId ?? string.Empty,
                        ApprovalRequestId = request.ToolApproval.ApprovalRequestId ?? string.Empty,
                    },
            },
            resumeService,
            ct);
    }

    private static async Task<IResult> HandleSignalDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        SignalScopeServiceRunHttpRequest request,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        CancellationToken ct)
    {
        var resolution = await ResolveScopedWorkflowRunAsync(
            http,
            scopeId,
            runId,
            request.ActorId,
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

    private static async Task<IResult> HandleStopDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        StopScopeServiceRunHttpRequest request,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        CancellationToken ct)
    {
        var resolution = await ResolveScopedWorkflowRunAsync(
            http,
            scopeId,
            runId,
            request.ActorId,
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

    private static async Task<IResult> HandleRetryCompensationDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        RetryCompensationScopeServiceRunHttpRequest request,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> retryService,
        CancellationToken ct)
    {
        var resolution = await ResolveScopedWorkflowRunAsync(
            http,
            scopeId,
            runId,
            request.ActorId,
            workflowRunBindingReader,
            ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        return await WorkflowCapabilityEndpoints.HandleRetryCompensation(
            new WorkflowRetryCompensationInput
            {
                ActorId = resolution.Binding!.ActorId,
                RunId = resolution.Binding.RunId,
                FailedCompensationStepId = request.FailedCompensationStepId ?? string.Empty,
                CommandId = request.CommandId,
                Reason = request.Reason,
            },
            retryService,
            ct);
    }

    private static async Task<IResult> HandleListMemberRunsAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        int take,
        string? scheduleId,
        string? status,
        string? updatedFrom,
        string? updatedTo,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceRunQueryPort serviceRunQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (TryCreateMemberRouteAccessDeniedResult(http, scopeId, memberId, out var denied))
                return denied;

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

            if (!TryBuildServiceRunQuery(
                    memberResolution.ScopeId,
                    memberResolution.PublishedServiceId,
                    take,
                    scheduleId,
                    status,
                    updatedFrom,
                    updatedTo,
                    out var query,
                    out var failure))
            {
                return failure!;
            }

            var snapshots = await serviceRunQueryPort.ListAsync(query!, ct);

            var summaries = new List<MemberScopeServiceRunSummaryHttpResponse>(snapshots.Count);
            foreach (var snapshot in snapshots)
            {
                var serviceSummary = await BuildScopeRunSummaryFromRegistryAsync(
                    memberResolution.ScopeId,
                    memberResolution.PublishedServiceId,
                    snapshot,
                    workflowRunBindingReader,
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
            if (TryCreateMemberRouteAccessDeniedResult(http, scopeId, memberId, out var denied))
                return denied;

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
            if (TryCreateMemberRouteAccessDeniedResult(http, scopeId, memberId, out var denied))
                return denied;

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
            var report = await workflowExecutionQueryService.GetWorkflowRunReportArtifactAsync(resolution.Binding!.ActorId, ct);
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
            if (TryCreateMemberRouteAccessDeniedResult(http, scopeId, memberId, out var denied))
                return denied;

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
            if (TryCreateMemberRouteAccessDeniedResult(http, scopeId, memberId, out var denied))
                return denied;

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
            if (TryCreateMemberRouteAccessDeniedResult(http, scopeId, memberId, out var denied))
                return denied;

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

    private static async Task<IResult> HandleRetryCompensationMemberRunAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string runId,
        RetryCompensationScopeServiceRunHttpRequest request,
        [FromServices] IMemberPublishedServiceResolver memberPublishedServiceResolver,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> retryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (TryCreateMemberRouteAccessDeniedResult(http, scopeId, memberId, out var denied))
                return denied;

            var memberResolution = await memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scopeId, memberId),
                ct);
            return await HandleRetryCompensationRunAsync(
                http,
                memberResolution.ScopeId,
                memberResolution.PublishedServiceId,
                runId,
                request,
                lifecycleQueryPort,
                workflowRunBindingReader,
                retryService,
                options,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_MEMBER_RUN_RETRY_COMPENSATION_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleListRunsAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        int take,
        string? scheduleId,
        string? status,
        string? updatedFrom,
        string? updatedTo,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceRunQueryPort serviceRunQueryPort,
        [FromServices] IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceAsync(http, scopeId, serviceId, lifecycleQueryPort, options.Value, ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        if (!TryBuildServiceRunQuery(
                scopeId,
                serviceId,
                take,
                scheduleId,
                status,
                updatedFrom,
                updatedTo,
                out var query,
                out var failure))
        {
            return failure!;
        }

        var snapshots = await serviceRunQueryPort.ListAsync(query!, ct);

        var summaries = new List<ScopeServiceRunSummaryHttpResponse>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            summaries.Add(await BuildScopeRunSummaryFromRegistryAsync(
                scopeId,
                serviceId,
                snapshot,
                workflowRunBindingReader: null,
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
            workflowRunBindingReader: null,
            workflowExecutionQueryService,
            ct));
    }

    private static async Task<IResult> HandleGetRunAuditAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string runId,
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
            workflowRunBindingReader: null,
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

        var report = await workflowExecutionQueryService.GetWorkflowRunReportArtifactAsync(snapshot.TargetActorId, ct);
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
            ScheduleId = invocationRequest.ScheduleId ?? string.Empty,
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

    private static Task HandleInvokeStreamAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string endpointId,
        WorkflowMultipartFileInputParser multipartFileInputParser,
        string? appId,
        [FromServices] ServiceInvocationResolutionService resolutionService,
        [FromServices] ServiceInvokeReadinessErrorMapper readinessErrorMapper,
        [FromServices] IInvokeAdmissionAuthorizer admissionAuthorizer,
        [FromServices] IServiceRunRegistrationPort serviceRunRegistrationPort,
        [FromServices] IWorkflowChatRunInteractionPort chatRunService,
        [FromServices] IFileArtifactIngressPort workflowFileIngressPort,
        [FromServices] ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>? scriptServiceRunService,
        [FromServices] IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleInvokeStreamCoreAsync(
            http,
            scopeId,
            serviceId,
            endpointId,
            multipartFileInputParser,
            appId,
            true,
            resolutionService,
            readinessErrorMapper,
            admissionAuthorizer,
            serviceRunRegistrationPort,
            chatRunService,
            workflowFileIngressPort,
            scriptServiceRunService,
            staticGAgentStreamInvocationPort,
            options,
            ct);

    private static async Task HandleInvokeStreamCoreAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string endpointId,
        WorkflowMultipartFileInputParser multipartFileInputParser,
        string? appId,
        bool allowEmptyInputForResolvedWorkflowService,
        [FromServices] ServiceInvocationResolutionService resolutionService,
        [FromServices] ServiceInvokeReadinessErrorMapper readinessErrorMapper,
        [FromServices] IInvokeAdmissionAuthorizer admissionAuthorizer,
        [FromServices] IServiceRunRegistrationPort serviceRunRegistrationPort,
        [FromServices] IWorkflowChatRunInteractionPort chatRunService,
        [FromServices] IFileArtifactIngressPort workflowFileIngressPort,
        [FromServices] ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>? scriptServiceRunService,
        [FromServices] IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            var logger = http.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger("Aevatar.GAgentService.Hosting.ScopeService");
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var requestInput = await ParseScopeStreamRequestAsync(http, multipartFileInputParser, ct);
            if (requestInput.Failure != null)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    requestInput.Failure.Value.StatusCode,
                    requestInput.Failure.Value.Code,
                    requestInput.Failure.Value.Message,
                    ct);
                return;
            }

            var request = requestInput.Request!;
            var normalizedPrompt = request.Prompt?.Trim() ?? string.Empty;
            var scopedHeaders = BuildScopedHeaders(request.Headers);
            var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct);
            if (!callerCredential.Succeeded)
            {
                var (statusCode, code, message) = ScopeWorkflowEndpoints.MapRunStartError(callerCredential.Error);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
                return;
            }

            var invocationRequest = BuildStreamInvocationRequest(
                options.Value,
                scopeId,
                serviceId,
                endpointId,
                normalizedPrompt,
                scopedHeaders,
                callerCredential.Credential,
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
                    var resolvedDefinitionBinding = BuildWorkflowStreamDefinitionBinding(
                        target,
                        invocationRequest,
                        scopeId);
                    var inputParts = MapInputParts(request.InputParts);
                    if (requestInput.MultipartForm is { HasFiles: true } multipartForm)
                    {
                        var uploadedParts = await IngestMultipartInputPartsAsync(
                            multipartForm,
                            workflowFileIngressPort,
                            scopeId,
                            ct);
                        inputParts = AppendInputParts(inputParts, uploadedParts);
                    }

                    var firstInputFileRef = FirstInputFileRef(inputParts);
                    logger?.LogWarning(
                        "Scope workflow stream input file refs resolved. scopeId={ScopeId} serviceId={ServiceId} endpointId={EndpointId} sessionId={SessionId} implementationKind={ImplementationKind} requestInputPartCount={RequestInputPartCount} mappedInputPartCount={MappedInputPartCount} multipartFileCount={MultipartFileCount} inputFileRefCount={InputFileRefCount} firstFileId={FirstFileId} firstArtifactId={FirstArtifactId} firstMediaType={FirstMediaType}",
                        scopeId,
                        serviceId,
                        endpointId,
                        request.SessionId ?? string.Empty,
                        target.Artifact.ImplementationKind,
                        request.InputParts?.Count ?? 0,
                        inputParts?.Count ?? 0,
                        requestInput.MultipartForm?.PendingFiles.Count ?? 0,
                        CountInputFileRefs(inputParts),
                        firstInputFileRef?.FileId ?? string.Empty,
                        firstInputFileRef?.ArtifactId ?? firstInputFileRef?.Uri ?? string.Empty,
                        firstInputFileRef?.MediaType ?? string.Empty);

                    await WorkflowCapabilityEndpoints.HandleChat(
                        http,
                        new ChatInput
                        {
                            Prompt = normalizedPrompt,
                            InputParts = inputParts,
                            Source = new WorkflowChatSourceInput
                            {
                                Kind = "definition_actor",
                                DefinitionActor = new WorkflowChatDefinitionActorSourceInput
                                {
                                    ActorId = target.Service.PrimaryActorId,
                                },
                            },
                            SessionId = request.SessionId,
                            ScopeId = scopeId,
                            Metadata = scopedHeaders,
                            Headers = scopedHeaders,
                            LlmControl = await BuildScopedLlmControlInputAsync(http, ct),
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
                            token),
                        allowEmptyInputForResolvedWorkflowService: allowEmptyInputForResolvedWorkflowService,
                        resolvedDefinitionBinding: resolvedDefinitionBinding);
                    break;

                case ServiceImplementationKind.Static:
                    EnsureNoMultipartFilesForNonWorkflowStream(requestInput);
                    await HandleStaticGAgentChatStreamAsync(
                        http,
                        normalizedPrompt,
                        request.ActorId,
                        request.SessionId,
                        scopedHeaders,
                        request.InputParts,
                        request.RevisionId,
                        invocationRequest,
                        staticGAgentStreamInvocationPort,
                        ct);
                    break;

                case ServiceImplementationKind.Scripting:
                    EnsureNoMultipartFilesForNonWorkflowStream(requestInput);
                    await HandleScriptingServiceChatStreamAsync(
                        http,
                        target,
                        normalizedPrompt,
                        request.SessionId,
                        scopeId,
                        serviceId,
                        scopedHeaders,
                        scriptServiceRunService,
                        invocationRequest,
                        ct);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Service implementation '{target.Artifact.ImplementationKind}' does not support SSE stream invocation.");
            }
        }
        catch (ServiceInvokeReadinessException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                readinessErrorMapper.Map(ex),
                ct);
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
        string prompt,
        string? actorId,
        string? sessionId,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyList<StreamContentPartHttpRequest>? inputParts,
        string? revisionId,
        ServiceInvocationRequest invocationRequest,
        IStaticGAgentStreamInvocationPort<AGUIEvent> staticGAgentStreamInvocationPort,
        CancellationToken ct)
    {
        // Refactor (iter39/cluster-039-scope-service-host-orchestration):
        //   Old pattern: Host built the static GAgent draft-run command, registered service-run state from the endpoint callback, and owned timeout/SSE lifecycle around that orchestration.
        //   New principle: Host only adapts HTTP/SSE callbacks; Application-owned IStaticGAgentStreamInvocationPort<AGUIEvent> owns static invocation and service-run registration semantics.
        await using var writer = new AGUISseWriter(http.Response);

        async ValueTask EmitAsync(AGUIEvent aguiEvent, CancellationToken token)
        {
            await writer.WriteAsync(aguiEvent, token);
        }

        async ValueTask OnAcceptedAsync(StaticGAgentStreamAcceptedReceipt receipt, CancellationToken token)
        {
            http.Response.Headers["X-Correlation-Id"] = receipt.GAgentReceipt.CorrelationId;
            await writer.StartAsync(token);
            await writer.WriteAsync(
                new AGUIEvent
                {
                    RunStarted = new RunStartedEvent
                    {
                        ThreadId = receipt.GAgentReceipt.ActorId,
                        RunId = receipt.GAgentReceipt.CommandId,
                    },
                },
                token);
        }

        try
        {
            var result = await staticGAgentStreamInvocationPort.InvokeAsync(
                new StaticGAgentStreamInvocationRequest(
                    invocationRequest.Identity?.Clone()
                        ?? throw new InvalidOperationException("service identity is required."),
                    invocationRequest.EndpointId,
                    new StaticGAgentStreamInvocationInput(
                        Prompt: prompt,
                        PreferredActorId: actorId,
                        SessionId: sessionId,
                        RevisionId: revisionId,
                        Headers: headers,
                        InputParts: MapGAgentDraftRunInputParts(inputParts),
                        Caller: invocationRequest.Caller?.Clone(),
                        Timeout: TimeSpan.FromMinutes(2))),
                EmitAsync,
                OnAcceptedAsync,
                ct);

            if (!result.Succeeded && result.StartError == GAgentDraftRunStartError.UnknownAgentKind)
            {
                throw new InvalidOperationException(
                    "GAgent kind could not be resolved.");
            }

            if (!result.Succeeded && result.StartError == GAgentDraftRunStartError.ActorKindMismatch)
            {
                throw new InvalidOperationException(
                    $"Actor '{actorId}' is not compatible with requested static GAgent service.");
            }

            if (!result.Succeeded && result.StartError == GAgentDraftRunStartError.ProjectionUnavailable)
            {
                if (!writer.ResponseStarted)
                {
                    await WriteJsonErrorResponseAsync(
                        http,
                        StatusCodes.Status503ServiceUnavailable,
                        "GAGENT_PROJECTION_UNAVAILABLE",
                        "GAgent projection is unavailable.",
                        ct);
                    return;
                }

                await writer.WriteAsync(
                    new AGUIEvent
                    {
                        RunError = new RunErrorEvent
                        {
                            Message = "GAgent projection is unavailable.",
                            Code = "GAGENT_PROJECTION_UNAVAILABLE",
                        },
                    },
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await writer.StartAsync(CancellationToken.None);
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
            if (!writer.ResponseStarted)
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

    // Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
    //   Old pattern: Scope service script stream inline orchestration in endpoints
    //   New principle: use existing ICommandInteractionService skeleton with ScriptServiceRunCommand and Application-owned service-run registration decorator
    private static async Task HandleScriptingServiceChatStreamAsync(
        HttpContext http,
        ServiceInvocationResolvedTarget target,
        string prompt,
        string? sessionId,
        string scopeId,
        string serviceId,
        IReadOnlyDictionary<string, string>? headers,
        ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>? interactionService,
        ServiceInvocationRequest invocationRequest,
        CancellationToken ct)
    {
        if (interactionService is null)
        {
            throw new InvalidOperationException(
                "Scripting capability is not enabled on this host; scripting services cannot be invoked.");
        }

        var actorId = target.Service.PrimaryActorId;
        var runId = Guid.NewGuid().ToString("N");
        var commandId = Guid.NewGuid().ToString("N");
        var correlationId = Guid.NewGuid().ToString("N");
        await using var writer = new AGUISseWriter(http.Response);

        async ValueTask EmitAsync(AGUIEvent aguiEvent, CancellationToken token)
        {
            await writer.WriteAsync(aguiEvent, token);
        }

        async ValueTask OnAcceptedAsync(ScriptServiceRunAcceptedReceipt receipt, CancellationToken token)
        {
            http.Response.Headers["X-Correlation-Id"] = receipt.CorrelationId;
            await writer.StartAsync(token);
            await writer.WriteAsync(new AGUIEvent
            {
                RunStarted = new RunStartedEvent { ThreadId = receipt.ActorId, RunId = receipt.RunId },
            }, token);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
            var result = await interactionService.ExecuteAsync(
                new ScriptServiceRunCommand(
                    ScopeId: scopeId,
                    ServiceId: serviceId,
                    ServiceKey: target.Service.ServiceKey ?? string.Empty,
                    EndpointId: target.Endpoint.EndpointId ?? string.Empty,
                    RevisionId: target.Service.RevisionId ?? string.Empty,
                    DeploymentId: target.Service.DeploymentId ?? string.Empty,
                    RuntimeActorId: actorId ?? string.Empty,
                    DefinitionActorId: target.Artifact.DeploymentPlan.ScriptingPlan.DefinitionActorId ?? string.Empty,
                    ScriptRevision: target.Artifact.DeploymentPlan.ScriptingPlan.Revision ?? string.Empty,
                    Prompt: prompt,
                    SessionId: sessionId,
                    RunId: runId,
                    CommandId: commandId,
                    CorrelationId: correlationId,
                    Headers: headers,
                    Identity: invocationRequest.Identity?.Clone()),
                EmitAsync,
                OnAcceptedAsync,
                timeoutCts.Token);
            if (!result.Succeeded)
            {
                throw result.Error?.ToException()
                    ?? new InvalidOperationException("Script service run failed to start.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await writer.StartAsync(CancellationToken.None);
            await writer.WriteAsync(new AGUIEvent
            {
                RunError = new RunErrorEvent { Message = "Script service chat stream timed out." },
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (!writer.ResponseStarted)
                throw;

            await writer.WriteAsync(new AGUIEvent
            {
                RunError = new RunErrorEvent { Message = ex.Message },
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
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        [FromServices] ServiceInvokeReadinessErrorMapper readinessErrorMapper,
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
            revisionCatalogReader,
            readinessErrorMapper,
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
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        ServiceInvokeReadinessErrorMapper readinessErrorMapper,
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
                revisionCatalogReader,
                ct);

            if (payload.Is(ChatRequestEvent.Descriptor))
            {
                var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct);
                if (!callerCredential.Succeeded)
                {
                    var (statusCode, code, message) =
                        ScopeWorkflowEndpoints.MapRunStartError(callerCredential.Error);
                    return Results.Json(new { code, message }, statusCode: statusCode);
                }

                payload = ProjectHttpCallerCredential(payload, callerCredential.Credential);
            }

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
            receipt.StatusUrl = BuildScopeServiceRunStatusUrl(
                scopeId,
                serviceId,
                receipt,
                acceptedResourcePath);
            return Results.Accepted(receipt.StatusUrl, receipt);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return ex is ServiceInvokeReadinessException readinessException
                ? Results.BadRequest(readinessErrorMapper.Map(readinessException))
                : CreateScopeInvokeFailureResult(ex);
        }
    }

    private static async Task<(Any Payload, string RevisionId)> ResolveInvocationPayloadAsync(
        InvokeScopeServiceHttpRequest request,
        string typeUrl,
        string requestedRevisionId,
        ServiceIdentity identity,
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
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

    private static string BuildScopeServiceRunStatusUrl(
        string scopeId,
        string serviceId,
        ServiceInvocationAcceptedReceipt receipt,
        string? acceptedResourcePath)
    {
        var basePath = string.IsNullOrWhiteSpace(acceptedResourcePath)
            ? BuildScopeServiceRunBasePath(scopeId, serviceId)
            : acceptedResourcePath.TrimEnd('/');
        return $"{basePath}/runs/{Uri.EscapeDataString(ResolveAcceptedRunId(receipt))}";
    }

    private static string BuildScopeServiceRunBasePath(
        string scopeId,
        string serviceId,
        string? memberId = null) =>
        string.IsNullOrWhiteSpace(memberId)
            ? $"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}"
            : $"/api/scopes/{Uri.EscapeDataString(scopeId)}/members/{Uri.EscapeDataString(memberId)}";

    private static string ResolveAcceptedRunId(ServiceInvocationAcceptedReceipt receipt) =>
        string.IsNullOrWhiteSpace(receipt.RunId) ? receipt.CommandId : receipt.RunId;

    private static bool TryCreateInvalidToolApprovalResumeRequest(
        ResumeScopeServiceRunHttpRequest request,
        out IResult result)
    {
        var hasFlatToolApprovalIdentity = request.ExtraFields?.Keys.Any(static key =>
            string.Equals(key, "executionId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "toolCallId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "approvalRequestId", StringComparison.OrdinalIgnoreCase)) == true;
        if (!hasFlatToolApprovalIdentity)
        {
            result = null!;
            return false;
        }

        result = Results.BadRequest(new
        {
            code = "INVALID_TOOL_APPROVAL_RESUME_REQUEST",
            message = "Tool approval identity must be nested under 'toolApproval'. Use " +
                      "{\"toolApproval\":{\"executionId\":\"...\",\"toolCallId\":\"...\",\"approvalRequestId\":\"...\"}}; " +
                      "top-level executionId, toolCallId and approvalRequestId are not accepted.",
        });
        return true;
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

        if (TryCreateInvalidToolApprovalResumeRequest(request, out var invalidRequest))
            return invalidRequest;

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
                ToolApproval = request.ToolApproval == null
                    ? null
                    : new WorkflowToolApprovalResumeInput
                    {
                        ExecutionId = request.ToolApproval.ExecutionId ?? string.Empty,
                        ToolCallId = request.ToolApproval.ToolCallId ?? string.Empty,
                        ApprovalRequestId = request.ToolApproval.ApprovalRequestId ?? string.Empty,
                    },
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

    private static async Task<IResult> HandleRetryCompensationRunAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string runId,
        RetryCompensationScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> retryService,
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

        return await WorkflowCapabilityEndpoints.HandleRetryCompensation(
            new WorkflowRetryCompensationInput
            {
                ActorId = resolution.Binding!.ActorId,
                RunId = resolution.Binding.RunId,
                FailedCompensationStepId = request.FailedCompensationStepId ?? string.Empty,
                CommandId = request.CommandId,
                Reason = request.Reason,
            },
            retryService,
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

    private static async Task<ScopeWorkflowRunResolution> ResolveScopedWorkflowRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        string? requestedActorId,
        IWorkflowRunBindingReader workflowRunBindingReader,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return new ScopeWorkflowRunResolution(null, denied);

        var normalizedRunId = ScopeWorkflowCapabilityOptions.NormalizeRequired(runId, nameof(runId));
        var matches = (await workflowRunBindingReader.ListByRunIdAsync(normalizedRunId, ct: ct))
            .Where(binding =>
                binding.ActorKind == WorkflowActorKind.Run &&
                string.Equals(binding.ScopeId, scopeId, StringComparison.Ordinal))
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
            return new ScopeWorkflowRunResolution(
                null,
                Results.NotFound(new
                {
                    code = "SCOPE_RUN_NOT_FOUND",
                    message = $"Run '{normalizedRunId}' was not found in scope '{scopeId}'.",
                }));
        }

        if (matches.Count > 1)
        {
            return new ScopeWorkflowRunResolution(
                null,
                Results.Conflict(new
                {
                    code = "SCOPE_RUN_AMBIGUOUS",
                    message = $"Run '{normalizedRunId}' is ambiguous in scope '{scopeId}'.",
                }));
        }

        return new ScopeWorkflowRunResolution(matches[0], null);
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
            revisions?.LastEventId ?? string.Empty,
            ExternalExposure: MapExternalExposure(service.ExternalExposure));
    }

    private static async Task<IReadOnlyList<ScopeServiceHttpResponse>> JoinScopeInvokeReadinessAsync(
        IReadOnlyList<ServiceCatalogSnapshot> services,
        IServiceInvocationCatalogQueryReader invocationCatalogQueryReader,
        CancellationToken ct)
    {
        var responses = new List<ScopeServiceHttpResponse>(services.Count);
        foreach (var service in services)
        {
            var catalog = await invocationCatalogQueryReader.GetAsync(new ServiceIdentity
            {
                TenantId = service.TenantId,
                AppId = service.AppId,
                Namespace = service.Namespace,
                ServiceId = service.ServiceId,
            }, ct);
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

            responses.Add(new ScopeServiceHttpResponse(
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
                MapExternalExposure(service.ExternalExposure)));
        }

        return responses;
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
            BuildScopeRevisionResponses(service, revisions, servingSet),
            ExternalExposure: MapExternalExposure(service.ExternalExposure));
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
                    revision.Implementation?.Static?.ActorTypeName ?? string.Empty,
                    revision.Implementation?.Static?.AgentKind ?? string.Empty);
            })
            .OrderByDescending(x => x.IsDefaultServing)
            .ThenByDescending(x => x.IsActiveServing)
            .ThenByDescending(x => x.PublishedAt ?? x.CreatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private static bool TryBuildServiceRunQuery(
        string scopeId,
        string serviceId,
        int take,
        string? scheduleId,
        string? status,
        string? updatedFrom,
        string? updatedTo,
        out ServiceRunQuery? query,
        out IResult? failure)
    {
        query = null;
        failure = null;
        if (!TryParseServiceRunStatus(status, out var parsedStatus, out var statusFailure))
        {
            failure = statusFailure;
            return false;
        }
        if (!TryParseDateTimeOffsetQuery(updatedFrom, nameof(updatedFrom), out var parsedUpdatedFrom, out var updatedFromFailure))
        {
            failure = updatedFromFailure;
            return false;
        }
        if (!TryParseDateTimeOffsetQuery(updatedTo, nameof(updatedTo), out var parsedUpdatedTo, out var updatedToFailure))
        {
            failure = updatedToFailure;
            return false;
        }

        query = new ServiceRunQuery(
            scopeId,
            serviceId,
            Math.Clamp(take <= 0 ? 50 : take, 1, 200),
            NormalizeOptionalQueryValue(scheduleId),
            parsedStatus,
            parsedUpdatedFrom,
            parsedUpdatedTo);
        return true;
    }

    private static bool TryParseServiceRunStatus(string? status, out ServiceRunStatus? parsed, out IResult? failure)
    {
        parsed = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(status))
            return true;

        if (System.Enum.TryParse<ServiceRunStatus>(status.Trim(), ignoreCase: true, out var enumValue) &&
            enumValue != ServiceRunStatus.Unspecified)
        {
            parsed = enumValue;
            return true;
        }

        failure = Results.BadRequest(new
        {
            code = "INVALID_SERVICE_RUN_QUERY",
            message = "status must be one of Accepted, Completed, Failed, or Stopped.",
        });
        return false;
    }

    private static bool TryParseDateTimeOffsetQuery(
        string? value,
        string parameterName,
        out DateTimeOffset? parsed,
        out IResult? failure)
    {
        parsed = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (DateTimeOffset.TryParse(value.Trim(), out var dateTimeOffset))
        {
            parsed = dateTimeOffset.ToUniversalTime();
            return true;
        }

        failure = Results.BadRequest(new
        {
            code = "INVALID_SERVICE_RUN_QUERY",
            message = $"{parameterName} must be a valid ISO-8601 date-time.",
        });
        return false;
    }

    private static string? NormalizeOptionalQueryValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        var snapshot = await workflowExecutionQueryService.GetWorkflowActorCurrentStateAsync(binding.ActorId, ct);
        var deployment = ResolveRunDeployment(binding, service, deployments);
        return new ScopeServiceRunSummaryHttpResponse(
            scopeId,
            serviceId,
            binding.RunId,
            string.Empty,
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
            snapshot?.SagaStatus ?? WorkflowSagaStatus.Unspecified,
            BuildScopeServiceRunDeadLetter(snapshot),
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
        IWorkflowRunBindingReader? workflowRunBindingReader,
        IWorkflowExecutionQueryApplicationService workflowExecutionQueryService,
        CancellationToken ct)
    {
        var workflowBinding = await ResolveWorkflowRunBindingAsync(snapshot, workflowRunBindingReader, ct);
        var workflowSnapshot = snapshot.ImplementationKind == ServiceImplementationKind.Workflow &&
                               !string.IsNullOrWhiteSpace(snapshot.TargetActorId)
            ? await workflowExecutionQueryService.GetWorkflowActorCurrentStateAsync(snapshot.TargetActorId, ct)
            : null;
        var registryBackedSummary = snapshot.ImplementationKind != ServiceImplementationKind.Workflow;

        return new ScopeServiceRunSummaryHttpResponse(
            scopeId,
            serviceId,
            snapshot.RunId,
            snapshot.ScheduleId,
            // ActorId stays the controllable target so existing resume/signal/stop
            // round-trips keep working; the registry actor is internal infra.
            snapshot.TargetActorId,
            workflowBinding?.EffectiveDefinitionActorId ?? string.Empty,
            snapshot.RevisionId,
            snapshot.DeploymentId,
            workflowSnapshot?.WorkflowName ?? string.Empty,
            workflowSnapshot?.CompletionStatus ??
            (registryBackedSummary ? MapServiceRunCompletionStatus(snapshot.Status) : WorkflowRunCompletionStatus.Unknown),
            workflowSnapshot?.StateVersion ?? snapshot.StateVersion,
            workflowSnapshot?.LastEventId ?? snapshot.LastEventId,
            workflowSnapshot?.LastUpdatedAt ?? snapshot.UpdatedAt,
            snapshot.CreatedAt,
            snapshot.UpdatedAt,
            workflowSnapshot?.LastSuccess ?? (registryBackedSummary ? MapServiceRunLastSuccess(snapshot.Status) : null),
            workflowSnapshot?.TotalSteps ?? 0,
            workflowSnapshot?.CompletedSteps ?? 0,
            workflowSnapshot?.RoleReplyCount ?? 0,
            workflowSnapshot?.LastOutput ?? (registryBackedSummary ? snapshot.LastOutput : string.Empty),
            workflowSnapshot?.LastError ?? (registryBackedSummary ? snapshot.LastError : string.Empty),
            workflowSnapshot?.SagaStatus ?? WorkflowSagaStatus.Unspecified,
            BuildScopeServiceRunDeadLetter(workflowSnapshot),
            snapshot.ImplementationKind.ToString(),
            snapshot.Status.ToString(),
            snapshot.CommandId,
            snapshot.CorrelationId,
            snapshot.EndpointId,
            snapshot.TargetActorId,
            snapshot.CreatedAt);
    }

    private static async Task<WorkflowActorBinding?> ResolveWorkflowRunBindingAsync(
        ServiceRunSnapshot snapshot,
        IWorkflowRunBindingReader? workflowRunBindingReader,
        CancellationToken ct)
    {
        if (workflowRunBindingReader == null ||
            snapshot.ImplementationKind != ServiceImplementationKind.Workflow ||
            string.IsNullOrWhiteSpace(snapshot.RunId))
        {
            return null;
        }

        var bindings = await workflowRunBindingReader.ListByRunIdAsync(snapshot.RunId, take: 20, ct);
        return bindings.FirstOrDefault(binding =>
            binding.ActorKind == WorkflowActorKind.Run &&
            string.Equals(binding.ActorId, snapshot.TargetActorId, StringComparison.Ordinal));
    }

    private static WorkflowRunCompletionStatus MapServiceRunCompletionStatus(ServiceRunStatus status) =>
        status switch
        {
            ServiceRunStatus.Accepted => WorkflowRunCompletionStatus.Running,
            ServiceRunStatus.Completed => WorkflowRunCompletionStatus.Completed,
            ServiceRunStatus.Failed => WorkflowRunCompletionStatus.Failed,
            ServiceRunStatus.Stopped => WorkflowRunCompletionStatus.Stopped,
            ServiceRunStatus.OutcomeUncertain => WorkflowRunCompletionStatus.Unknown,
            _ => WorkflowRunCompletionStatus.Unknown,
        };

    private static bool? MapServiceRunLastSuccess(ServiceRunStatus status) =>
        status switch
        {
            ServiceRunStatus.Completed => true,
            ServiceRunStatus.Failed => false,
            ServiceRunStatus.Stopped => false,
            ServiceRunStatus.OutcomeUncertain => null,
            _ => null,
        };

    private static MemberScopeServiceRunSummaryHttpResponse BuildMemberRunSummaryResponse(
        MemberPublishedServiceResolution memberResolution,
        ScopeServiceRunSummaryHttpResponse summary)
    {
        return new MemberScopeServiceRunSummaryHttpResponse(
            summary.ScopeId,
            memberResolution.MemberId,
            memberResolution.PublishedServiceId,
            summary.RunId,
            summary.ScheduleId,
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
            summary.LastError,
            summary.SagaStatus,
            summary.DeadLetter,
            summary.ImplementationKind,
            summary.Status,
            summary.CommandId,
            summary.CorrelationId,
            summary.EndpointId,
            summary.TargetActorId,
            summary.CreatedAt);
    }

    private static ScopeServiceRunDeadLetterHttpResponse? BuildScopeServiceRunDeadLetter(
        WorkflowActorSnapshot? snapshot)
    {
        if (snapshot?.SagaStatus != WorkflowSagaStatus.CompensationDeadLetter)
            return null;

        return new ScopeServiceRunDeadLetterHttpResponse(
            snapshot.DeadLetterFailedCompensationStepId,
            snapshot.DeadLetterRemainingUncompensated,
            snapshot.DeadLetterError);
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
        WorkflowCallerCredential? callerCredential,
        string? revisionId,
        string? appId = null)
    {
        var payload = new ChatRequestEvent
        {
            Prompt = prompt,
            ScopeId = scopeId,
            ConnectorHttpAuthorization = ToConnectorHttpAuthorization(callerCredential),
            CallerNyxIdCredentialKind = ToAgentToolNyxIdCredentialKind(callerCredential?.Kind),
            CallerSourceReadableNyxIdBearerToken =
                callerCredential?.SourceReadableUserBearerToken?.Trim() ?? string.Empty,
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

    private static string ToConnectorHttpAuthorization(WorkflowCallerCredential? callerCredential)
    {
        var token = callerCredential?.BearerToken?.Trim();
        return string.IsNullOrWhiteSpace(token) ? string.Empty : $"Bearer {token}";
    }

    private static Any ProjectHttpCallerCredential(
        Any payload,
        WorkflowCallerCredential? callerCredential)
    {
        var sanitized = ScheduledServiceInvocationPayloadPolicy
            .StripScheduleOwnedCredentialFields(payload)
            .Unpack<ChatRequestEvent>();
        sanitized.ConnectorHttpAuthorization = ToConnectorHttpAuthorization(callerCredential);
        sanitized.CallerNyxIdCredentialKind = ToAgentToolNyxIdCredentialKind(callerCredential?.Kind);
        sanitized.CallerSourceReadableNyxIdBearerToken =
            callerCredential?.SourceReadableUserBearerToken?.Trim() ?? string.Empty;
        return Any.Pack(sanitized);
    }

    private static AgentToolNyxIdCredentialKindPayload ToAgentToolNyxIdCredentialKind(
        Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind? kind) => kind switch
        {
            Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.SourceReadableUserBearer =>
                AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
            Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.ProxyDelegation =>
                AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
            Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.AgentKey =>
                AgentToolNyxIdCredentialKindPayload.AgentKey,
            _ => AgentToolNyxIdCredentialKindPayload.Unspecified,
        };

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

    private static WorkflowDefinitionBinding BuildWorkflowStreamDefinitionBinding(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        string scopeId)
    {
        var plan = target.Artifact.DeploymentPlan?.WorkflowPlan
            ?? throw new InvalidOperationException("Workflow service deployment plan is required.");
        var bindingIdentity = WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
            target.Artifact,
            target.Service.RevisionId);
        return new WorkflowDefinitionBinding(
            ResolveWorkflowServiceDefinitionActorId(target, plan),
            plan.WorkflowName,
            plan.WorkflowYaml,
            plan.InlineWorkflowYamls,
            plan.ExecutionMode,
            scopeId.Trim(),
            string.IsNullOrWhiteSpace(request.RunOrigin)
                ? WorkflowRunOrigins.ServiceInvoke
                : request.RunOrigin.Trim(),
            request.ScheduleId?.Trim() ?? string.Empty,
            SourceKind: "service_revision",
            CapabilityAdmissionPlan: plan.CapabilityAdmissionPlan?.Clone(),
            WorkflowId: bindingIdentity.WorkflowId,
            RevisionId: bindingIdentity.RevisionId,
            ToolCatalogPolicyVersion: plan.ToolCatalogPolicyVersion);
    }

    private static string ResolveWorkflowServiceDefinitionActorId(
        ServiceInvocationResolvedTarget target,
        WorkflowServiceDeploymentPlan plan)
    {
        var serviceDefinitionActorId = target.Service.PrimaryActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(serviceDefinitionActorId))
            return serviceDefinitionActorId;

        return plan.DefinitionActorId?.Trim() ?? string.Empty;
    }

    private static Dictionary<string, string> BuildScopedHeaders(
        IReadOnlyDictionary<string, string>? headers)
    {
        var scopedHeaders = headers == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        scopedHeaders.Remove("scope_id");
        scopedHeaders.Remove(WorkflowRunCommandMetadataKeys.ScopeId);
        scopedHeaders.Remove(LegacyConnectorHttpAuthorizationBlockedKey);
        // Refactor (iter169/cluster-issue1551): Old pattern: scoped headers carried connector auth metadata. New principle: headers stay annotations; connector auth uses typed workflow command/proto fields.
        return scopedHeaders;
    }

    private static async Task<ChatLlmControlInput?> BuildScopedLlmControlInputAsync(
        HttpContext? http,
        CancellationToken cancellationToken = default)
    {
        var control = await BuildScopedLlmControlAsync(http, cancellationToken);
        if (control == null)
            return null;

        return new ChatLlmControlInput
        {
            ModelOverride = control.ModelOverride,
            NyxIdRoutePreference = control.NyxIdRoutePreference,
            MaxToolRoundsOverride = control.MaxToolRoundsOverride,
            UserMemoryPrompt = control.UserMemoryPrompt,
        };
    }

    private static async Task<LLMControlContext?> BuildScopedLlmControlAsync(
        HttpContext? http,
        CancellationToken cancellationToken = default)
    {
        if (http == null)
            return null;

        var control = new LLMControlContext(
            NyxIdAccessToken: null,
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            ModelOverride: null,
            NyxIdRoutePreference: null,
            MaxToolRoundsOverride: null,
            UserMemoryPrompt: null);

        var userConfigStore = http.RequestServices.GetService<IUserConfigQueryPort>();
        if (userConfigStore != null)
        {
            try
            {
                var userConfig = await userConfigStore.GetAsync(cancellationToken);
                var model = string.IsNullOrWhiteSpace(userConfig.DefaultModel)
                    ? control.ModelOverride
                    : userConfig.DefaultModel.Trim();
                var route = UserLlmSelectionRoute.Resolve(userConfig.LlmSelection);

                control = control with
                {
                    ModelOverride = model,
                    NyxIdRoutePreference = route,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var loggerFactory = http.RequestServices.GetService<ILoggerFactory>();
                var logger = loggerFactory?.CreateLogger("Aevatar.GAgentService.ScopeServiceEndpoints");
                logger?.LogWarning(ex, "Failed to resolve scoped user LLM configuration; falling back to provider defaults.");
            }
        }

        return control == LLMControlContext.Empty ? null : control;
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

    private static async ValueTask<ScopeStreamRequestInput> ParseScopeStreamRequestAsync(
        HttpContext http,
        WorkflowMultipartFileInputParser multipartFileInputParser,
        CancellationToken ct)
    {
        if (WorkflowMultipartFileInputParser.IsMultipartForm(http.Request.ContentType))
        {
            var multipartResult = await multipartFileInputParser.ParseAsync(http, ct);
            if (!multipartResult.Succeeded)
                return ScopeStreamRequestInput.Failed(ToScopeStreamRequestError(multipartResult.Error!.Value));

            var request = ParseScopeStreamPayload(multipartResult.RawPayloadJson);
            if (request == null)
                return ScopeStreamRequestInput.Failed(ScopeStreamRequestParseError.InvalidRequest);

            return ScopeStreamRequestInput.Success(request, multipartResult.Form);
        }

        if (!IsJsonContentType(http.Request.ContentType))
            return ScopeStreamRequestInput.Failed(ScopeStreamRequestParseError.UnsupportedMediaType);

        StreamScopeServiceHttpRequest? parsed;
        try
        {
            parsed = await JsonSerializer.DeserializeAsync<StreamScopeServiceHttpRequest>(
                http.Request.Body,
                ScopeRequestJsonOptions,
                ct);
        }
        catch (JsonException)
        {
            return ScopeStreamRequestInput.Failed(ScopeStreamRequestParseError.InvalidRequest);
        }

        return parsed == null
            ? ScopeStreamRequestInput.Failed(ScopeStreamRequestParseError.InvalidRequest)
            : ScopeStreamRequestInput.Success(parsed, null);
    }

    private static StreamScopeServiceHttpRequest? ParseScopeStreamPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new StreamScopeServiceHttpRequest(null);

        try
        {
            return JsonSerializer.Deserialize<StreamScopeServiceHttpRequest>(payload, ScopeRequestJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsJsonContentType(string? contentType) =>
        contentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;

    private static ScopeStreamRequestParseError ToScopeStreamRequestError(
        WorkflowMultipartFileInputParseError error) =>
        new(error.StatusCode, error.Code, error.Message);

    private static ScopeDraftRunRequestParseError ToScopeDraftRunRequestError(
        WorkflowMultipartFileInputParseError error)
    {
        if (error.StatusCode == StatusCodes.Status415UnsupportedMediaType)
            return ScopeDraftRunRequestParseError.UnsupportedMediaType;

        return string.Equals(error.Code, "INVALID_FILE_INPUT", StringComparison.Ordinal)
            ? ScopeDraftRunRequestParseError.InvalidFileInput
            : ScopeDraftRunRequestParseError.InvalidRequest;
    }

    private static ChatLlmControlInput? ToChatLlmControlInput(LLMControlContext? control)
    {
        if (control == null)
            return null;

        return new ChatLlmControlInput
        {
            ModelOverride = control.ModelOverride,
            NyxIdRoutePreference = control.NyxIdRoutePreference,
            MaxToolRoundsOverride = control.MaxToolRoundsOverride,
            UserMemoryPrompt = control.UserMemoryPrompt,
        };
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
                InlineFile = p.InlineFile,
                FileRef = p.FileRef,
            }).ToList();
    }

    private static int CountInputFileRefs(IReadOnlyList<ChatInputContentPart>? inputParts) =>
        inputParts?.Count(static part => part.FileRef is not null && HasFileRefIdentity(part.FileRef)) ?? 0;

    private static ChatInputFileRef? FirstInputFileRef(IReadOnlyList<ChatInputContentPart>? inputParts) =>
        inputParts?.FirstOrDefault(static part => part.FileRef is not null && HasFileRefIdentity(part.FileRef))?.FileRef;

    private static bool HasFileRefIdentity(ChatInputFileRef fileRef) =>
        !string.IsNullOrWhiteSpace(fileRef.FileId) ||
        !string.IsNullOrWhiteSpace(fileRef.ArtifactId) ||
        !string.IsNullOrWhiteSpace(fileRef.Uri);

    private static IReadOnlyList<ChatInputContentPart>? AppendInputParts(
        IReadOnlyList<ChatInputContentPart>? existing,
        IReadOnlyList<ChatInputContentPart> appended)
    {
        if (appended.Count == 0)
            return existing;

        if (existing is not { Count: > 0 })
            return appended;

        var inputParts = new List<ChatInputContentPart>(existing.Count + appended.Count);
        inputParts.AddRange(existing);
        inputParts.AddRange(appended);
        return inputParts;
    }

    private static async ValueTask<IReadOnlyList<ChatInputContentPart>> IngestMultipartInputPartsAsync(
        WorkflowMultipartFileInputForm form,
        IFileArtifactIngressPort workflowFileIngressPort,
        string scopeId,
        CancellationToken ct)
    {
        var inputParts = new List<ChatInputContentPart>(form.PendingFiles.Count);
        foreach (var file in form.PendingFiles)
        {
            FileArtifactIngressResult ingressResult;
            try
            {
                ingressResult = await workflowFileIngressPort.IngestAsync(
                    WorkflowMultipartFileInputParser.BuildIngressRequest(file, scopeId),
                    ct);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("Multipart chat file input is invalid.", ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("Multipart chat file input is invalid.", ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("Multipart chat file input is invalid.", ex);
            }

            inputParts.Add(WorkflowMultipartFileInputParser.BuildInputPart(file, ingressResult.FileRef));
        }

        return inputParts;
    }

    private static void EnsureNoMultipartFilesForNonWorkflowStream(ScopeStreamRequestInput requestInput)
    {
        if (requestInput.MultipartForm?.HasFiles == true)
            throw new InvalidOperationException("Multipart file input is only supported for workflow services.");
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

    private static IResult CreateTeamEntryFailureResult(TeamEntryMemberResolutionException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var statusCode = ResolveTeamEntryHttpStatusCode(ex.Code);
        return Results.Json(
            new
            {
                code = ex.Code,
                message = ex.Message,
            },
            statusCode: statusCode);
    }

    private static int ResolveTeamEntryHttpStatusCode(string code) =>
        code switch
        {
            TeamEntryMemberErrorCodes.TeamNotFound => StatusCodes.Status404NotFound,
            TeamEntryMemberErrorCodes.TeamArchived => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberNotConfigured => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberMismatch => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberNotReady => StatusCodes.Status409Conflict,
            TeamEntryMemberErrorCodes.EntryMemberNotFound => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

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
        var workflowYamls = request.Workflow?.WorkflowYamls;
        return workflowYamls == null
            ? null
            : new ScopeBindingWorkflowSpec(request.Workflow?.WorkflowId ?? string.Empty, workflowYamls);
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

    private static bool TryCreateMemberRouteAccessDeniedResult(
        HttpContext http,
        string scopeId,
        string memberId,
        out IResult denied)
    {
        return AevatarMemberAccessGuard.TryCreateMemberAccessDeniedResult(
            http,
            scopeId,
            memberId,
            out denied);
    }

    private static async Task<bool> TryWriteMemberRouteAccessDeniedAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        CancellationToken ct)
    {
        return await AevatarMemberAccessGuard.TryWriteMemberAccessDeniedAsync(
            http,
            scopeId,
            memberId,
            ct);
    }

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

    private static async Task WriteJsonErrorResponseAsync(
        HttpContext http,
        int statusCode,
        object body,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(body, cancellationToken: ct);
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
        string? EventFormat = null,
        IReadOnlyList<StreamContentPartHttpRequest>? InputParts = null);

    public sealed record UpsertScopeBindingHttpRequest(
        string ImplementationKind,
        // Refactor (iter165/cluster-007):
        //   Old pattern: top-level WorkflowYamls was a fallback for workflow.workflowYamls.
        //   New principle: inline workflow documents live only under the workflow variant.
        IReadOnlyList<string>? WorkflowYamls = null,
        ScopeBindingWorkflowHttpRequest? Workflow = null,
        ScopeBindingScriptHttpRequest? Script = null,
        ScopeBindingGAgentHttpRequest? GAgent = null,
        string? DisplayName = null,
        string? RevisionId = null,
        string? AppId = null,
        string? ServiceId = null,
        bool? ExposureDesired = null);

    public sealed record ScopeBindingWorkflowHttpRequest(
        string? WorkflowId,
        IReadOnlyList<string>? WorkflowYamls);

    public sealed record ScopeBindingScriptHttpRequest(
        string ScriptId,
        string? ScriptRevision = null);

    public sealed record ScopeBindingGAgentHttpRequest(
        string AgentKind,
        IReadOnlyList<ServiceEndpoints.ServiceEndpointHttpRequest>? Endpoints)
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtraFields { get; init; }

        public bool HasLegacyActorTypeName =>
            ExtraFields?.Keys.Any(key =>
                string.Equals(key, "actorTypeName", StringComparison.Ordinal) ||
                string.Equals(key, "ActorTypeName", StringComparison.Ordinal)) == true;
    }

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
        string? Name = null,
        ChatInputInlineFile? InlineFile = null,
        ChatInputFileRef? FileRef = null);

    public sealed record ResumeScopeServiceRunHttpRequest(
        string? StepId,
        string? CommandId,
        bool Approved,
        string? UserInput = null,
        Dictionary<string, string>? Metadata = null,
        string? ActorId = null,
        WorkflowToolApprovalResumeHttpRequest? ToolApproval = null)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtraFields { get; init; }
    }

    public sealed record WorkflowToolApprovalResumeHttpRequest(
        string? ExecutionId,
        string? ToolCallId,
        string? ApprovalRequestId);

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

    public sealed record RetryCompensationScopeServiceRunHttpRequest(
        string? FailedCompensationStepId,
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

    private sealed record ScopeWorkflowRunResolution(
        WorkflowActorBinding? Binding,
        IResult? Failure);

    private sealed record ScopeDraftRunRequestInput(
        ScopeDraftRunHttpRequest? Request,
        IReadOnlyList<ChatInputContentPart>? InputParts,
        ScopeDraftRunRequestParseError? Failure)
    {
        public static ScopeDraftRunRequestInput Success(
            ScopeDraftRunHttpRequest request,
            IReadOnlyList<ChatInputContentPart>? inputParts) =>
            new(request, inputParts, null);

        public static ScopeDraftRunRequestInput Failed(ScopeDraftRunRequestParseError error) =>
            new(null, null, error);
    }

    private readonly record struct ScopeDraftRunRequestParseError(
        int StatusCode,
        string Code,
        string Message)
    {
        public static readonly ScopeDraftRunRequestParseError UnsupportedMediaType = new(
            StatusCodes.Status415UnsupportedMediaType,
            "UNSUPPORTED_MEDIA_TYPE",
            "Content-Type must be application/json or multipart/form-data.");

        public static readonly ScopeDraftRunRequestParseError InvalidRequest = new(
            StatusCodes.Status400BadRequest,
            "INVALID_SCOPE_DRAFT_RUN_REQUEST",
            "Multipart draft-run payload is invalid.");

        public static readonly ScopeDraftRunRequestParseError InvalidFileInput = new(
            StatusCodes.Status400BadRequest,
            "INVALID_FILE_INPUT",
            "Multipart chat file input is invalid.");
    }

    private sealed record ScopeStreamRequestInput(
        StreamScopeServiceHttpRequest? Request,
        WorkflowMultipartFileInputForm? MultipartForm,
        ScopeStreamRequestParseError? Failure)
    {
        public static ScopeStreamRequestInput Success(
            StreamScopeServiceHttpRequest request,
            WorkflowMultipartFileInputForm? multipartForm) =>
            new(request, multipartForm, null);

        public static ScopeStreamRequestInput Failed(ScopeStreamRequestParseError error) =>
            new(null, null, error);
    }

    private readonly record struct ScopeStreamRequestParseError(
        int StatusCode,
        string Code,
        string Message)
    {
        public static readonly ScopeStreamRequestParseError UnsupportedMediaType = new(
            StatusCodes.Status415UnsupportedMediaType,
            "UNSUPPORTED_MEDIA_TYPE",
            "Content-Type must be application/json or multipart/form-data.");

        public static readonly ScopeStreamRequestParseError InvalidRequest = new(
            StatusCodes.Status400BadRequest,
            "INVALID_SERVICE_STREAM_REQUEST",
            "Service stream request body is invalid.");
    }

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
        string CatalogLastEventId = "",
        ExternalExposureHttpResponse? ExternalExposure = null);

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
        string StaticActorTypeName = "",
        string StaticAgentKind = "");

    public sealed record ScopeBindingActivationHttpResponse(
        string ScopeId,
        string ServiceId,
        string DisplayName,
        string RevisionId);

    public sealed record ScopeServiceHttpResponse(
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
        IReadOnlyList<ScopeBindingRevisionHttpResponse> Revisions,
        ExternalExposureHttpResponse? ExternalExposure = null);

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
        string ScheduleId,
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
        WorkflowSagaStatus SagaStatus,
        ScopeServiceRunDeadLetterHttpResponse? DeadLetter,
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
        string ScheduleId,
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
        WorkflowSagaStatus SagaStatus,
        ScopeServiceRunDeadLetterHttpResponse? DeadLetter,
        string ImplementationKind,
        string Status,
        string CommandId,
        string CorrelationId,
        string EndpointId,
        string TargetActorId,
        DateTimeOffset? CreatedAt = null);

    public sealed record ScopeServiceRunDeadLetterHttpResponse(
        string FailedCompensationStepId,
        int RemainingUncompensated,
        string Error);

    public sealed record ScopeServiceRunAuditHttpResponse(
        ScopeServiceRunSummaryHttpResponse Summary,
        WorkflowRunReport Audit);

    public sealed record MemberScopeServiceRunAuditHttpResponse(
        MemberScopeServiceRunSummaryHttpResponse Summary,
        WorkflowRunReport Audit);
}
