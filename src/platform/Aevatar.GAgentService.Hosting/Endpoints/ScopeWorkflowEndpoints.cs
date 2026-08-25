using Aevatar.AGUI.Contracts;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Capabilities;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Hosting.Sse;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ScopeWorkflowEndpoints
{
    private const string LegacyConnectorHttpAuthorizationBlockedKey = "connector.http.authorization";

    public static IEndpointRouteBuilder MapScopeWorkflowCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = ScopeEndpointRouteGroups.MapScopeGroup(app).WithTags("ScopeWorkflows");
        group.MapPut("/{scopeId}/workflows/{workflowId}", HandleUpsertWorkflowAsync)
            .Produces<ScopeWorkflowUpsertResult>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/{scopeId}/workflows:save-and-bind", HandleSaveAndBindWorkflowAsync)
            .Produces<ScopeWorkflowSaveAndBindResult>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/{scopeId}/workflows/{workflowId}:archive", HandleArchiveWorkflowAsync)
            .Produces<ScopeWorkflowArchiveAcceptedResult>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{scopeId}/workflows:explicit-request-preview", HandleExplicitRequestPreviewAsync)
            .Produces<ExplicitRequestPreviewHttpResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapGet("/{scopeId}/workflows", HandleListWorkflowsAsync)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        if (app.ServiceProvider.GetService<IAppScopedWorkflowCatalogueService>() != null)
        {
            group.MapGet("/{scopeId}/workflow-catalogue", HandleQueryWorkflowCatalogueAsync)
                .Produces<ScopeWorkflowCatalogueResponse>(StatusCodes.Status200OK)
                .Produces(StatusCodes.Status400BadRequest);
        }

        group.MapGet("/{scopeId}/workflows/{workflowId}", HandleGetWorkflowDetailAsync)
            .Produces<ScopeWorkflowDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapScopeWorkflowScheduleEndpoints();
        return app;
    }

    internal static async Task<IResult> HandleUpsertWorkflowAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        UpsertScopeWorkflowHttpRequest request,
        [FromServices] IScopeWorkflowCommandPort workflowCommandPort,
        CancellationToken ct)
        => await HandleUpsertWorkflowAsyncCore(http, scopeId, workflowId, request, workflowCommandPort, ct);

    internal static async Task<IResult> HandleSaveAndBindWorkflowAsync(
        HttpContext http,
        string scopeId,
        SaveAndBindScopeWorkflowHttpRequest request,
        [FromServices] IScopeWorkflowSaveAndBindPort saveAndBindPort,
        CancellationToken ct)
        => await HandleSaveAndBindWorkflowAsyncCore(http, scopeId, request, saveAndBindPort, ct);

    internal static async Task<IResult> HandleArchiveWorkflowAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        [FromServices] IScopeWorkflowArchiveCommandPort archiveCommandPort,
        CancellationToken ct)
        => await HandleArchiveWorkflowAsyncCore(http, scopeId, workflowId, archiveCommandPort, ct);

    internal static async Task<IResult> HandleExplicitRequestPreviewAsync(
        HttpContext http,
        string scopeId,
        ExplicitRequestPreviewHttpRequest request,
        [FromServices] IWorkflowExplicitRequestPreviewService previewService,
        CancellationToken ct)
        => await HandleExplicitRequestPreviewAsyncCore(http, scopeId, request, previewService, ct);

    internal static async Task<IResult> HandleListWorkflowsAsync(
        HttpContext http,
        string scopeId,
        bool includeSource,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
        => await HandleListWorkflowsAsyncCore(http, scopeId, includeSource, workflowQueryPort, workflowActorBindingReader, revisionCatalogReader, options, ct);

    internal static async Task<IResult> HandleQueryWorkflowCatalogueAsync(
        HttpContext http,
        string scopeId,
        string? view,
        string? query,
        string? cursor,
        int? take,
        [FromServices] IAppScopedWorkflowCatalogueService catalogueService,
        CancellationToken ct)
        => await HandleQueryWorkflowCatalogueAsyncCore(http, scopeId, view, query, cursor, take, catalogueService, ct);

    internal static async Task<IResult> HandleGetWorkflowDetailAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
        => await HandleGetWorkflowDetailAsyncCore(http, scopeId, workflowId, workflowQueryPort, workflowActorBindingReader, revisionCatalogReader, options, ct);

    internal static async Task HandleRunWorkflowByIdStreamAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        RunScopeWorkflowByIdStreamHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowChatRunInteractionPort chatRunService,
        CancellationToken ct)
        => await HandleRunWorkflowByIdStreamAsyncCore(http, scopeId, workflowId, request, workflowQueryPort, chatRunService, ct);

    internal static async Task HandleRunWorkflowStreamAsync(
        HttpContext http,
        string scopeId,
        RunScopeWorkflowStreamHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowChatRunInteractionPort chatRunService,
        CancellationToken ct)
        => await HandleRunWorkflowStreamAsyncCore(http, scopeId, request, workflowQueryPort, chatRunService, ct);

    private static async Task<IResult> HandleUpsertWorkflowAsyncCore(
        HttpContext http,
        string scopeId,
        string workflowId,
        UpsertScopeWorkflowHttpRequest request,
        IScopeWorkflowCommandPort workflowCommandPort,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var result = await workflowCommandPort.UpsertAsync(new ScopeWorkflowUpsertRequest(
                scopeId,
                workflowId,
                request.WorkflowYaml,
                request.WorkflowName,
                request.DisplayName,
                request.InlineWorkflowYamls,
                request.RevisionId)
            {
                CapabilityAdmission = await WorkflowCapabilityAdmissionHttpContext.CreateAsync(
                    http,
                    ParseSaveAndBindExecutionMode(request.ExecutionMode),
                    explicitRequestConfirmations: request.ExplicitRequestConfirmations,
                    ct: ct),
            }, ct);
            return Results.Accepted(result.ReadModelUrl, result);
        }
        catch (NyxIdExplicitRequestConfirmationInputException ex)
        {
            return ExplicitRequestConfirmationBadRequest(ex);
        }
        catch (WorkflowCallerCredentialSelectionException)
        {
            return CallerCredentialBadRequest();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_USER_WORKFLOW_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleSaveAndBindWorkflowAsyncCore(
        HttpContext http,
        string scopeId,
        SaveAndBindScopeWorkflowHttpRequest request,
        IScopeWorkflowSaveAndBindPort saveAndBindPort,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var result = await saveAndBindPort.SaveAndBindAsync(
                new ScopeWorkflowSaveAndBindRequest(
                    scopeId,
                    request.WorkflowId,
                    request.WorkflowYaml,
                    request.WorkflowName,
                    request.DisplayName,
                    request.InlineWorkflowYamls,
                    request.AppId,
                    request.ServiceId,
                    request.ExposureDesired,
                    request.RevisionId)
                {
                    CapabilityAdmission = await WorkflowCapabilityAdmissionHttpContext.CreateAsync(
                        http,
                        ParseSaveAndBindExecutionMode(request.ExecutionMode),
                        explicitRequestConfirmations: request.ExplicitRequestConfirmations,
                        ct: ct),
                },
                ct);
            return Results.Accepted(result.Workflow.ReadModelUrl, result);
        }
        catch (NyxIdExplicitRequestConfirmationInputException ex)
        {
            return ExplicitRequestConfirmationBadRequest(ex);
        }
        catch (WorkflowCallerCredentialSelectionException)
        {
            return CallerCredentialBadRequest();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_USER_WORKFLOW_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleArchiveWorkflowAsyncCore(
        HttpContext http,
        string scopeId,
        string workflowId,
        IScopeWorkflowArchiveCommandPort archiveCommandPort,
        CancellationToken ct)
    {
        if (TryCreateArchiveRequestBadRequest(scopeId, workflowId, out var badRequest, out var normalizedScopeId, out var normalizedWorkflowId))
            return badRequest;

        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, normalizedScopeId, out var denied))
                return denied;

            var result = await archiveCommandPort.ArchiveAsync(
                new ScopeWorkflowArchiveRequest(normalizedScopeId, normalizedWorkflowId),
                ct);
            return Results.Accepted(result.ReadModelUrl, result);
        }
        catch (ScopeWorkflowArchiveRejectedException ex)
        {
            var statusCode = ex.Kind == ScopeWorkflowArchiveRejectionKind.NotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
            return Results.Json(new
            {
                code = ex.Code,
                message = ex.Message,
            }, statusCode: statusCode);
        }
    }

    private static bool TryCreateArchiveRequestBadRequest(
        string scopeId,
        string workflowId,
        out IResult badRequest,
        out string normalizedScopeId,
        out string normalizedWorkflowId)
    {
        try
        {
            normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
            normalizedWorkflowId = ScopeWorkflowCapabilityOptions.NormalizeRequired(workflowId, nameof(workflowId));
            if (normalizedWorkflowId.Contains(':', StringComparison.Ordinal))
                throw new InvalidOperationException("workflowId must not contain ':'.");

            badRequest = Results.Empty;
            return false;
        }
        catch (InvalidOperationException ex)
        {
            normalizedScopeId = string.Empty;
            normalizedWorkflowId = string.Empty;
            badRequest = Results.BadRequest(new
            {
                code = "INVALID_USER_WORKFLOW_ARCHIVE_REQUEST",
                message = ex.Message,
            });
            return true;
        }
    }

    private static IResult ExplicitRequestConfirmationBadRequest(
        NyxIdExplicitRequestConfirmationInputException exception) =>
        Results.BadRequest(new
        {
            code = NyxIdExplicitRequestConfirmationInputException.ErrorCode,
            message = exception.Message,
        });

    private static IResult CallerCredentialBadRequest() =>
        Results.BadRequest(new
        {
            code = WorkflowCallerCredentialSelectionException.ErrorCode,
            message = WorkflowCallerCredentialSelectionException.SafeMessage,
        });

    private static async Task<IResult> HandleExplicitRequestPreviewAsyncCore(
        HttpContext http,
        string scopeId,
        ExplicitRequestPreviewHttpRequest request,
        IWorkflowExplicitRequestPreviewService previewService,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var executionMode = ParseExplicitRequestPreviewExecutionMode(request.ExecutionMode);
            var admissionContext = await WorkflowCapabilityAdmissionHttpContext.CreateAsync(
                http,
                executionMode,
                ct: ct);
            var result = await previewService.PreviewAsync(
                new WorkflowExplicitRequestPreviewRequest(
                    new ExternalWorkflowCapabilityAccessContext(
                        scopeId,
                        admissionContext.CallerId,
                        admissionContext.NyxIdCallerCredential,
                        admissionContext.NyxIdOrganizationBearerToken),
                    request.WorkflowYaml,
                    request.InlineWorkflowYamls,
                    executionMode,
                    request.WorkflowId,
                    request.RevisionId),
                ct);

            return Results.Ok(new ExplicitRequestPreviewHttpResult(
                result.WorkflowId,
                result.RevisionId,
                result.Items.Select(ToExplicitRequestPreviewHttpItem).ToArray()));
        }
        catch (WorkflowCallerCredentialSelectionException)
        {
            return CallerCredentialBadRequest();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_USER_WORKFLOW_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static ExternalCapabilityExecutionMode ParseExplicitRequestPreviewExecutionMode(string? value) =>
        value?.Trim() switch
        {
            "interactive" => ExternalCapabilityExecutionMode.Interactive,
            "durable" => ExternalCapabilityExecutionMode.Durable,
            _ => throw new InvalidOperationException(
                "ExecutionMode must be either 'interactive' or 'durable'."),
        };

    private static ExternalCapabilityExecutionMode ParseSaveAndBindExecutionMode(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ExternalCapabilityExecutionMode.Interactive
            : ParseExplicitRequestPreviewExecutionMode(value);

    private static ExplicitRequestPreviewHttpItem ToExplicitRequestPreviewHttpItem(
        WorkflowExplicitRequestPreviewItem item) =>
        new(
            item.CallSiteId,
            item.RequestContractDigest,
            item.UserServiceId,
            ToWireValue(item.Method),
            item.PathTemplate,
            ToWireValue(item.BodyMode),
            item.BodyRequired,
            ToWireValue(item.ResponseMode),
            ToWireValue(item.EffectiveRisk),
            item.ApprovalRequired,
            ToWireValue(item.ApprovalEnforcement),
            item.AllowedExecutionModes.Select(ToWireValue).ToArray());

    private static string ToWireValue(WorkflowExplicitRequestApprovalEnforcement value) =>
        value switch
        {
            WorkflowExplicitRequestApprovalEnforcement.BindTimeConfirmationAndRunTimeToolApproval =>
                "bind_time_confirmation_and_run_time_tool_approval",
            WorkflowExplicitRequestApprovalEnforcement.None => "none",
            _ => throw new InvalidOperationException(
                "Explicit request approval enforcement is invalid."),
        };

    private static string ToWireValue(NyxIdRequestMethod value) => value switch
    {
        NyxIdRequestMethod.Get => "get",
        NyxIdRequestMethod.Head => "head",
        NyxIdRequestMethod.Options => "options",
        NyxIdRequestMethod.Post => "post",
        NyxIdRequestMethod.Put => "put",
        NyxIdRequestMethod.Patch => "patch",
        NyxIdRequestMethod.Delete => "delete",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdRequestBodyMode value) => value switch
    {
        NyxIdRequestBodyMode.None => "none",
        NyxIdRequestBodyMode.Json => "json",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdRequestResponseMode value) => value switch
    {
        NyxIdRequestResponseMode.Text => "text",
        NyxIdRequestResponseMode.FileArtifact => "file_artifact",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdOperationRisk value) => value switch
    {
        NyxIdOperationRisk.ReadOnly => "read_only",
        NyxIdOperationRisk.Write => "write",
        NyxIdOperationRisk.Destructive => "destructive",
        _ => "unspecified",
    };

    private static string ToWireValue(ExternalCapabilityExecutionMode value) => value switch
    {
        ExternalCapabilityExecutionMode.Interactive => "interactive",
        ExternalCapabilityExecutionMode.Durable => "durable",
        _ => "unspecified",
    };

    private static async Task<IResult> HandleListWorkflowsAsyncCore(
        HttpContext http,
        string scopeId,
        bool includeSource,
        IScopeWorkflowQueryPort workflowQueryPort,
        IWorkflowActorBindingReader workflowActorBindingReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var workflows = await workflowQueryPort.ListAsync(scopeId, ct);
            if (!includeSource)
                return Results.Ok(workflows);

            var details = new List<ScopeWorkflowDetail>(workflows.Count);
            foreach (var workflow in workflows)
                details.Add(await BuildWorkflowDetailAsync(workflow, workflowActorBindingReader, revisionCatalogReader, ct));

            return Results.Ok(details);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_USER_WORKFLOW_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleQueryWorkflowCatalogueAsyncCore(
        HttpContext http,
        string scopeId,
        string? view,
        string? query,
        string? cursor,
        int? take,
        IAppScopedWorkflowCatalogueService catalogueService,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            if (!TryParseCatalogueView(view, out var catalogueView))
            {
                return Results.BadRequest(new
                {
                    code = "INVALID_WORKFLOW_CATALOGUE_REQUEST",
                    message = "view must be either 'all', 'drafts', or 'archived'.",
                });
            }

            return Results.Ok(await catalogueService.QueryAsync(
                new ScopeWorkflowCatalogueQuery(scopeId, catalogueView, query, cursor, take ?? 0),
                ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_WORKFLOW_CATALOGUE_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleGetWorkflowDetailAsyncCore(
        HttpContext http,
        string scopeId,
        string workflowId,
        IScopeWorkflowQueryPort workflowQueryPort,
        IWorkflowActorBindingReader workflowActorBindingReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var lookup = await workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct);
            if (!lookup.IsRunnable)
            {
                var (statusCode, code, message) = MapWorkflowLookupError(scopeId, workflowId, lookup);
                return Results.Json(
                    new
                    {
                        code,
                        message,
                    },
                    statusCode: statusCode);
            }

            return Results.Json(await BuildWorkflowDetailAsync(lookup.Workflow!, workflowActorBindingReader, revisionCatalogReader, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_USER_WORKFLOW_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task HandleRunWorkflowByIdStreamAsyncCore(
        HttpContext http,
        string scopeId,
        string workflowId,
        RunScopeWorkflowByIdStreamHttpRequest request,
        IScopeWorkflowQueryPort workflowQueryPort,
        IWorkflowChatRunInteractionPort chatRunService,
        CancellationToken ct)
    {
        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var lookup = await workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct);
            if (!lookup.IsRunnable)
            {
                var (statusCode, code, message) = MapWorkflowLookupError(scopeId, workflowId, lookup);
                await WriteJsonErrorResponseAsync(
                    http,
                    statusCode,
                    code,
                    message,
                    ct);
                return;
            }

            await HandleRunWorkflowStreamCoreAsync(
                http,
                scopeId,
                lookup.Workflow!,
                request.Prompt,
                request.SessionId,
                request.Headers,
                request.EventFormat,
                chatRunService,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_USER_WORKFLOW_REQUEST",
                ex.Message,
                ct);
        }
    }

    private static async Task HandleRunWorkflowStreamAsyncCore(
        HttpContext http,
        string scopeId,
        RunScopeWorkflowStreamHttpRequest request,
        IScopeWorkflowQueryPort workflowQueryPort,
        IWorkflowChatRunInteractionPort chatRunService,
        CancellationToken ct)
    {
        try
        {
            if (await AevatarScopeAccessGuard.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var workflow = await workflowQueryPort.GetByActorIdAsync(scopeId, request.ActorId, ct);
            if (workflow == null)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    "USER_WORKFLOW_NOT_FOUND",
                    BuildWorkflowActorNotFoundMessage(scopeId),
                    ct);
                return;
            }

            await HandleRunWorkflowStreamCoreAsync(
                http,
                scopeId,
                workflow,
                request.Prompt,
                request.SessionId,
                request.Headers,
                request.EventFormat,
                chatRunService,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_USER_WORKFLOW_REQUEST",
                ex.Message,
                ct);
        }
    }

    private static async Task HandleRunWorkflowStreamCoreAsync(
        HttpContext http,
        string scopeId,
        ScopeWorkflowSummary workflow,
        string prompt,
        string? sessionId,
        Dictionary<string, string>? headers,
        string? eventFormat,
        IWorkflowChatRunInteractionPort chatRunService,
        CancellationToken ct)
    {
        if (!TryParseEventFormat(eventFormat, out var resolvedEventFormat))
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_USER_WORKFLOW_REQUEST",
                "eventFormat must be either 'workflow' or 'agui'.",
                ct);
            return;
        }

        if (resolvedEventFormat == ScopeWorkflowStreamEventFormat.Workflow)
        {
            var scopedHeaders = BuildScopedHeaders(headers);
            await WorkflowCapabilityEndpoints.HandleChat(
                http,
                new ChatInput
                {
                    Prompt = prompt,
                    Source = new WorkflowChatSourceInput
                    {
                        Kind = "definition_actor",
                        DefinitionActor = new WorkflowChatDefinitionActorSourceInput
                        {
                            ActorId = workflow.ActorId,
                        },
                    },
                    SessionId = sessionId,
                    ScopeId = NormalizeRequired(scopeId, nameof(scopeId)),
                    Headers = scopedHeaders,
                    LlmControl = await BuildScopedLlmControlInputAsync(http, ct),
                },
                chatRunService,
                ct);
            return;
        }

        var aguiHeaders = BuildScopedHeaders(headers);
        await HandleAguiStreamAsync(
            http,
            scopeId,
            workflow,
            prompt,
            sessionId,
            aguiHeaders,
            await BuildScopedLlmControlAsync(http, ct),
            chatRunService,
            ct);
    }

    internal static async Task HandleAguiStreamAsync(
        HttpContext http,
        WorkflowChatRunRequest request,
        IWorkflowChatRunInteractionPort chatRunService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);

        await using var writer = new AGUISseWriter(http.Response, ScopeWorkflowAguiEventMapper.TypeRegistry);

        try
        {
            var result = await chatRunService.ExecuteAsync(
                request,
                async (frame, token) =>
                {
                    if (!ScopeWorkflowAguiEventMapper.TryMap(frame, out var aguiEvent) || aguiEvent == null)
                        return;

                    await writer.StartAsync(token);
                    await writer.WriteAsync(aguiEvent, token);
                },
                async (receipt, token) =>
                {
                    if (!string.IsNullOrWhiteSpace(receipt.Run.CorrelationId))
                        http.Response.Headers["X-Correlation-Id"] = receipt.Run.CorrelationId;

                    await writer.StartAsync(token);
                    await writer.WriteAsync(ScopeWorkflowAguiEventMapper.BuildRunContextEvent(receipt.Run), token);
                },
                ct);

            if (!result.Succeeded && !writer.ResponseStarted)
            {
                if (result.FailureDetail?.ExternalCapabilityReadiness is not null)
                {
                    await WriteJsonErrorResponseAsync(
                        http,
                        ChatRunStartErrorMapper.ToHttpStatusCode(result.Error),
                        ChatRunStartErrorMapper.ToErrorBody(result.FailureDetail),
                        ct);
                    return;
                }

                var (statusCode, code, message) = MapRunStartError(result.Error);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!writer.ResponseStarted)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status500InternalServerError,
                    "EXECUTION_FAILED",
                    "Workflow execution failed.",
                    CancellationToken.None);
                return;
            }

            await writer.WriteAsync(ScopeWorkflowAguiEventMapper.BuildRunErrorEvent(ex), CancellationToken.None);
        }
    }

    private static async Task HandleAguiStreamAsync(
        HttpContext http,
        string scopeId,
        ScopeWorkflowSummary workflow,
        string prompt,
        string? sessionId,
        IReadOnlyDictionary<string, string>? headers,
        LLMControlContext? llmControl,
        IWorkflowChatRunInteractionPort chatRunService,
        CancellationToken ct)
    {
        prompt = string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt.Trim();
        var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct);
        if (!callerCredential.Succeeded)
        {
            var (statusCode, code, message) = MapRunStartError(callerCredential.Error);
            await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
            return;
        }

        await HandleAguiStreamAsync(
            http,
            new WorkflowChatRunRequest(
                prompt,
                WorkflowChatSource.DefinitionActor(workflow.ActorId, workflow.WorkflowName),
                ExternalCapabilityExecutionMode.Interactive,
                sessionId,
                Metadata: headers,
                ScopeId: NormalizeRequired(scopeId, nameof(scopeId)),
                CallerCredential: callerCredential.Credential,
                LlmControl: ToWorkflowLlmControl(llmControl),
                Headers: headers),
            chatRunService,
            ct);
    }

    private static async Task<ScopeWorkflowDetail> BuildWorkflowDetailAsync(
        ScopeWorkflowSummary workflow,
        IWorkflowActorBindingReader workflowActorBindingReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct)
    {
        PreparedServiceRevisionArtifact? artifact = null;
        WorkflowActorBinding? binding = null;
        if (!string.IsNullOrWhiteSpace(workflow.ActorId))
            binding = await workflowActorBindingReader.GetAsync(workflow.ActorId, ct);

        if (!string.IsNullOrWhiteSpace(workflow.ServiceKey) &&
            !string.IsNullOrWhiteSpace(workflow.ActiveRevisionId))
        {
            var revisionCatalog = await revisionCatalogReader.GetAsync(BuildWorkflowServiceIdentity(workflow), ct);
            artifact = revisionCatalog?.Revisions
                .FirstOrDefault(x => string.Equals(x.RevisionId, workflow.ActiveRevisionId, StringComparison.Ordinal))
                ?.PreparedArtifact
                ?.Clone();
        }

        return BuildWorkflowDetailPayload(workflow, binding, artifact);
    }

    private static ServiceIdentity BuildWorkflowServiceIdentity(ScopeWorkflowSummary workflow) =>
        new()
        {
            TenantId = ScopeWorkflowCapabilityOptions.NormalizeRequired(workflow.ScopeId, nameof(workflow.ScopeId)),
            AppId = ScopeWorkflowCapabilityOptions.NormalizeRequired(workflow.ServiceAppId, nameof(workflow.ServiceAppId)),
            Namespace = ScopeWorkflowCapabilityOptions.NormalizeRequired(workflow.ServiceNamespace, nameof(workflow.ServiceNamespace)),
            ServiceId = ScopeWorkflowCapabilityOptions.NormalizeRequired(workflow.PublishedServiceId, nameof(workflow.PublishedServiceId)),
        };

    private static ScopeWorkflowDetail BuildWorkflowDetailPayload(
        ScopeWorkflowSummary workflow,
        WorkflowActorBinding? binding,
        PreparedServiceRevisionArtifact? artifact)
    {
        var workflowPlan = artifact?.DeploymentPlan?.WorkflowPlan;
        var hasBindingSource = binding?.HasDefinitionPayload == true;
        return new ScopeWorkflowDetail(
            true,
            workflow.ScopeId,
            workflow,
            !hasBindingSource && workflowPlan == null
                ? null
                : new ScopeWorkflowSource(
                    hasBindingSource
                        ? binding!.WorkflowYaml
                        : workflowPlan!.WorkflowYaml,
                    hasBindingSource
                        ? binding!.EffectiveDefinitionActorId
                        : workflowPlan!.DefinitionActorId,
                    hasBindingSource
                        ? binding!.InlineWorkflowYamls
                        : workflowPlan!.InlineWorkflowYamls));
    }

    private static string BuildWorkflowNotFoundMessage(
        string scopeId,
        string workflowId) =>
        $"Workflow '{workflowId}' was not found for scope '{scopeId}'.";

    private static string BuildWorkflowActorNotFoundMessage(string scopeId) =>
        $"Workflow actor was not found for scope '{scopeId}'.";

    internal static (int StatusCode, string Code, string Message) MapWorkflowLookupError(
        string scopeId,
        string workflowId,
        ScopeWorkflowLookupResult lookup) =>
        lookup.Status switch
        {
            ScopeWorkflowLookupStatus.NotFound => (
                StatusCodes.Status404NotFound,
                "USER_WORKFLOW_NOT_FOUND",
                BuildWorkflowNotFoundMessage(scopeId, workflowId)),
            ScopeWorkflowLookupStatus.Stale => (
                StatusCodes.Status409Conflict,
                "USER_WORKFLOW_STALE",
                $"Workflow '{workflowId}' runtime readmodel is stale for scope '{scopeId}'."),
            _ => (
                StatusCodes.Status409Conflict,
                "USER_WORKFLOW_NOT_READY",
                $"Workflow '{workflowId}' is not ready to run for scope '{scopeId}'."),
        };

    internal static bool TryParseCatalogueView(
        string? rawValue,
        out ScopeWorkflowCatalogueView view)
    {
        if (string.IsNullOrWhiteSpace(rawValue) || string.Equals(rawValue, "all", StringComparison.OrdinalIgnoreCase))
        {
            view = ScopeWorkflowCatalogueView.All;
            return true;
        }

        if (string.Equals(rawValue, "drafts", StringComparison.OrdinalIgnoreCase))
        {
            view = ScopeWorkflowCatalogueView.Drafts;
            return true;
        }

        if (string.Equals(rawValue, "archived", StringComparison.OrdinalIgnoreCase))
        {
            view = ScopeWorkflowCatalogueView.Archived;
            return true;
        }

        view = ScopeWorkflowCatalogueView.All;
        return false;
    }

    internal static bool TryParseEventFormat(
        string? rawValue,
        out ScopeWorkflowStreamEventFormat eventFormat)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            eventFormat = ScopeWorkflowStreamEventFormat.Workflow;
            return true;
        }

        if (string.Equals(rawValue, "workflow", StringComparison.OrdinalIgnoreCase))
        {
            eventFormat = ScopeWorkflowStreamEventFormat.Workflow;
            return true;
        }

        if (string.Equals(rawValue, "agui", StringComparison.OrdinalIgnoreCase))
        {
            eventFormat = ScopeWorkflowStreamEventFormat.Agui;
            return true;
        }

        eventFormat = ScopeWorkflowStreamEventFormat.Workflow;
        return false;
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

        return scopedHeaders;
    }

    internal static async Task<ChatLlmControlInput?> BuildScopedLlmControlInputAsync(
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

    private static WorkflowLlmControl? ToWorkflowLlmControl(LLMControlContext? control)
    {
        if (control == null)
            return null;

        var model = NormalizeOptional(control.ModelOverride);
        var userMemoryPrompt = NormalizeOptional(control.UserMemoryPrompt);
        var routePreference = NormalizeOptional(control.NyxIdRoutePreference);
        var maxToolRounds = control.MaxToolRoundsOverride is > 0
            ? control.MaxToolRoundsOverride
            : null;
        if (model == null && userMemoryPrompt == null && routePreference == null && maxToolRounds == null)
            return null;

        return new WorkflowLlmControl(
            ModelOverride: model,
            MaxToolRoundsOverride: maxToolRounds,
            UserMemoryPrompt: userMemoryPrompt,
            RoutePreference: routePreference);
    }

    internal static async Task<LLMControlContext?> BuildScopedLlmControlAsync(
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
                var logger = loggerFactory?.CreateLogger("Aevatar.GAgentService.ScopeWorkflowEndpoints");
                logger?.LogWarning(ex, "Failed to resolve scoped user LLM configuration; falling back to provider defaults.");
            }
        }

        return control == LLMControlContext.Empty ? null : control;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static (int StatusCode, string Code, string Message) MapRunStartError(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.AgentNotFound => (StatusCodes.Status404NotFound, "AGENT_NOT_FOUND", "Agent not found."),
            WorkflowChatRunStartError.WorkflowNotFound => (StatusCodes.Status404NotFound, "WORKFLOW_NOT_FOUND", "Workflow not found."),
            WorkflowChatRunStartError.AgentTypeNotSupported => (StatusCodes.Status400BadRequest, "AGENT_TYPE_NOT_SUPPORTED", "Actor is not workflow-capable."),
            WorkflowChatRunStartError.ProjectionDisabled => (StatusCodes.Status503ServiceUnavailable, "PROJECTION_DISABLED", "Projection pipeline is disabled."),
            WorkflowChatRunStartError.ProjectionUnavailable => (StatusCodes.Status503ServiceUnavailable, "WORKFLOW_PROJECTION_UNAVAILABLE", "Workflow projection is unavailable."),
            WorkflowChatRunStartError.WorkflowBindingMismatch => (StatusCodes.Status409Conflict, "WORKFLOW_BINDING_MISMATCH", "Actor is bound to a different workflow."),
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => (StatusCodes.Status409Conflict, "AGENT_WORKFLOW_NOT_CONFIGURED", "Actor has no bound workflow."),
            WorkflowChatRunStartError.InvalidWorkflowYaml => (StatusCodes.Status400BadRequest, "INVALID_WORKFLOW_YAML", "Workflow YAML is invalid."),
            WorkflowChatRunStartError.ExternalCapabilityNotReady => (StatusCodes.Status409Conflict, "EXTERNAL_WORKFLOW_CAPABILITY_NOT_READY", "External workflow capability admission failed."),
            WorkflowChatRunStartError.WorkflowNameMismatch => (StatusCodes.Status400BadRequest, "WORKFLOW_NAME_MISMATCH", "Workflow name does not match workflow YAML."),
            WorkflowChatRunStartError.PromptRequired => (StatusCodes.Status400BadRequest, "PROMPT_REQUIRED", "Prompt is required."),
            WorkflowChatRunStartError.InvalidCallerCredential => (StatusCodes.Status400BadRequest, "INVALID_CALLER_CREDENTIAL", "Caller credential is invalid."),
            _ => (StatusCodes.Status400BadRequest, "RUN_START_FAILED", "Failed to resolve actor."),
        };
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

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{paramName} is required.");

        return normalized;
    }

    public sealed record UpsertScopeWorkflowHttpRequest(
        string WorkflowYaml,
        string? WorkflowName = null,
        string? DisplayName = null,
        Dictionary<string, string>? InlineWorkflowYamls = null,
        string? RevisionId = null,
        IReadOnlyList<NyxIdExplicitRequestConfirmationInput>? ExplicitRequestConfirmations = null,
        string? ExecutionMode = null);

    public sealed record SaveAndBindScopeWorkflowHttpRequest(
        string? WorkflowId,
        string WorkflowYaml,
        string? WorkflowName = null,
        string? DisplayName = null,
        Dictionary<string, string>? InlineWorkflowYamls = null,
        string? AppId = null,
        string? ServiceId = null,
        bool? ExposureDesired = null,
        string? RevisionId = null,
        IReadOnlyList<NyxIdExplicitRequestConfirmationInput>? ExplicitRequestConfirmations = null,
        string? ExecutionMode = null);

    public sealed record ExplicitRequestPreviewHttpRequest(
        string WorkflowYaml,
        string ExecutionMode,
        Dictionary<string, string>? InlineWorkflowYamls = null,
        string? WorkflowId = null,
        string? RevisionId = null);

    public sealed record ExplicitRequestPreviewHttpResult(
        string WorkflowId,
        string RevisionId,
        IReadOnlyList<ExplicitRequestPreviewHttpItem> Items);

    public sealed record ExplicitRequestPreviewHttpItem(
        string CallSiteId,
        string RequestContractDigest,
        string UserServiceId,
        string Method,
        string PathTemplate,
        string BodyMode,
        bool BodyRequired,
        string ResponseMode,
        string EffectiveRisk,
        bool ApprovalRequired,
        string ApprovalEnforcement,
        IReadOnlyList<string> AllowedExecutionModes);

    public sealed record RunScopeWorkflowByIdStreamHttpRequest(
        string Prompt,
        string? SessionId = null,
        Dictionary<string, string>? Headers = null,
        string? EventFormat = null);

    public sealed record RunScopeWorkflowStreamHttpRequest(
        string ActorId,
        string Prompt,
        string? SessionId = null,
        Dictionary<string, string>? Headers = null,
        string? EventFormat = null);

    internal enum ScopeWorkflowStreamEventFormat
    {
        Workflow = 0,
        Agui = 1,
    }
}
