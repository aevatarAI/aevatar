using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ScopeWorkflowEndpoints
{
    private static readonly string[] ScopeClaimTypes =
    [
        WorkflowRunCommandMetadataKeys.ScopeId,
        "scope_id",
    ];

    public static IEndpointRouteBuilder MapScopeWorkflowCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("ScopeWorkflows");
        group.MapPut("/{scopeId}/workflows/{workflowId}", HandleUpsertWorkflowAsync)
            .Produces<ScopeWorkflowUpsertResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPut("/{scopeId}/apps/{appId}/workflows/{workflowId}", HandleAppWorkflowUpsertAsync)
            .Produces<ScopeWorkflowUpsertResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapGet("/{scopeId}/workflows", HandleListWorkflowsAsync)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapGet("/{scopeId}/apps/{appId}/workflows", HandleAppWorkflowListAsync)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapGet("/{scopeId}/workflows/{workflowId}", HandleGetWorkflowDetailAsync)
            .Produces<ScopeWorkflowDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapGet("/{scopeId}/apps/{appId}/workflows/{workflowId}", HandleAppWorkflowDetailAsync)
            .Produces<ScopeWorkflowDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/workflows/{workflowId}/runs:stream", HandleRunWorkflowByIdStreamAsync)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/apps/{appId}/workflows/{workflowId}/runs:stream", HandleAppWorkflowByIdStreamAsync)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/workflow-runs:stream", HandleRunWorkflowStreamAsync)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/apps/{appId}/workflow-runs:stream", HandleAppWorkflowStreamAsync)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/workflow-runs/stop", HandleStopWorkflowRunAsync)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/apps/{appId}/workflow-runs/stop", HandleAppWorkflowStopAsync)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        return app;
    }

    internal static async Task<IResult> HandleUpsertWorkflowAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        UpsertScopeWorkflowHttpRequest request,
        [FromServices] IScopeWorkflowCommandPort workflowCommandPort,
        CancellationToken ct)
        => await HandleUpsertWorkflowAsyncCore(http, scopeId, appId: null, workflowId, request, workflowCommandPort, ct);

    internal static async Task<IResult> HandleListWorkflowsAsync(
        HttpContext http,
        string scopeId,
        bool includeSource,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] IServiceRevisionArtifactStore? artifactStore,
        CancellationToken ct)
        => await HandleListWorkflowsAsyncCore(http, scopeId, appId: null, includeSource, workflowQueryPort, workflowActorBindingReader, artifactStore, ct);

    internal static async Task<IResult> HandleGetWorkflowDetailAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] IServiceRevisionArtifactStore? artifactStore,
        CancellationToken ct)
        => await HandleGetWorkflowDetailAsyncCore(http, scopeId, appId: null, workflowId, workflowQueryPort, workflowActorBindingReader, artifactStore, ct);

    internal static async Task HandleRunWorkflowByIdStreamAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        RunScopeWorkflowByIdStreamHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
        => await HandleRunWorkflowByIdStreamAsyncCore(http, scopeId, appId: null, workflowId, request, workflowQueryPort, chatRunService, ct);

    internal static async Task HandleRunWorkflowStreamAsync(
        HttpContext http,
        string scopeId,
        RunScopeWorkflowStreamHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
        => await HandleRunWorkflowStreamAsyncCore(http, scopeId, appId: null, request, workflowQueryPort, chatRunService, ct);

    internal static async Task<IResult> HandleStopWorkflowRunAsync(
        HttpContext http,
        string scopeId,
        StopScopeWorkflowRunHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        CancellationToken ct)
        => await HandleStopWorkflowRunAsyncCore(http, scopeId, appId: null, request, workflowQueryPort, workflowActorBindingReader, stopService, ct);

    internal static async Task<IResult> HandleAppWorkflowUpsertAsync(
        HttpContext http,
        string scopeId,
        string appId,
        string workflowId,
        UpsertScopeWorkflowHttpRequest request,
        [FromServices] IScopeWorkflowCommandPort workflowCommandPort,
        CancellationToken ct)
        => await HandleUpsertWorkflowAsyncCore(http, scopeId, appId, workflowId, request, workflowCommandPort, ct);

    internal static async Task<IResult> HandleAppWorkflowListAsync(
        HttpContext http,
        string scopeId,
        string appId,
        bool includeSource,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] IServiceRevisionArtifactStore? artifactStore,
        CancellationToken ct)
        => await HandleListWorkflowsAsyncCore(http, scopeId, appId, includeSource, workflowQueryPort, workflowActorBindingReader, artifactStore, ct);

    internal static async Task<IResult> HandleAppWorkflowDetailAsync(
        HttpContext http,
        string scopeId,
        string appId,
        string workflowId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] IServiceRevisionArtifactStore? artifactStore,
        CancellationToken ct)
        => await HandleGetWorkflowDetailAsyncCore(http, scopeId, appId, workflowId, workflowQueryPort, workflowActorBindingReader, artifactStore, ct);

    internal static async Task HandleAppWorkflowByIdStreamAsync(
        HttpContext http,
        string scopeId,
        string appId,
        string workflowId,
        RunScopeWorkflowByIdStreamHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
        => await HandleRunWorkflowByIdStreamAsyncCore(http, scopeId, appId, workflowId, request, workflowQueryPort, chatRunService, ct);

    internal static async Task HandleAppWorkflowStreamAsync(
        HttpContext http,
        string scopeId,
        string appId,
        RunScopeWorkflowStreamHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
        => await HandleRunWorkflowStreamAsyncCore(http, scopeId, appId, request, workflowQueryPort, chatRunService, ct);

    internal static async Task<IResult> HandleAppWorkflowStopAsync(
        HttpContext http,
        string scopeId,
        string appId,
        StopScopeWorkflowRunHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        CancellationToken ct)
        => await HandleStopWorkflowRunAsyncCore(http, scopeId, appId, request, workflowQueryPort, workflowActorBindingReader, stopService, ct);

    private static async Task<IResult> HandleUpsertWorkflowAsyncCore(
        HttpContext http,
        string scopeId,
        string? appId,
        string workflowId,
        UpsertScopeWorkflowHttpRequest request,
        IScopeWorkflowCommandPort workflowCommandPort,
        CancellationToken ct)
    {
        try
        {
            if (TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var result = await workflowCommandPort.UpsertAsync(new ScopeWorkflowUpsertRequest(
                scopeId,
                workflowId,
                request.WorkflowYaml,
                request.WorkflowName,
                request.DisplayName,
                request.InlineWorkflowYamls,
                request.RevisionId,
                appId), ct);
            return Results.Ok(result);
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

    private static async Task<IResult> HandleListWorkflowsAsyncCore(
        HttpContext http,
        string scopeId,
        string? appId,
        bool includeSource,
        IScopeWorkflowQueryPort workflowQueryPort,
        IWorkflowActorBindingReader workflowActorBindingReader,
        IServiceRevisionArtifactStore? artifactStore,
        CancellationToken ct)
    {
        try
        {
            if (TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var workflows = string.IsNullOrWhiteSpace(appId)
                ? await workflowQueryPort.ListAsync(scopeId, ct)
                : await workflowQueryPort.ListAsync(scopeId, appId, ct);
            if (!includeSource)
                return Results.Ok(workflows);

            var details = new List<ScopeWorkflowDetail>(workflows.Count);
            foreach (var workflow in workflows)
                details.Add(await BuildWorkflowDetailAsync(workflow, workflowActorBindingReader, artifactStore, ct));

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

    private static async Task<IResult> HandleGetWorkflowDetailAsyncCore(
        HttpContext http,
        string scopeId,
        string? appId,
        string workflowId,
        IScopeWorkflowQueryPort workflowQueryPort,
        IWorkflowActorBindingReader workflowActorBindingReader,
        IServiceRevisionArtifactStore? artifactStore,
        CancellationToken ct)
    {
        try
        {
            if (TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var workflow = string.IsNullOrWhiteSpace(appId)
                ? await workflowQueryPort.GetByWorkflowIdAsync(scopeId, workflowId, ct)
                : await workflowQueryPort.GetByWorkflowIdAsync(scopeId, appId, workflowId, ct);
            if (workflow == null)
            {
                return Results.NotFound(new
                {
                    code = "USER_WORKFLOW_NOT_FOUND",
                    message = BuildWorkflowNotFoundMessage(scopeId, workflowId, appId),
                });
            }

            return Results.Json(await BuildWorkflowDetailAsync(workflow, workflowActorBindingReader, artifactStore, ct));
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
        string? appId,
        string workflowId,
        RunScopeWorkflowByIdStreamHttpRequest request,
        IScopeWorkflowQueryPort workflowQueryPort,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
    {
        try
        {
            if (await TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var workflow = string.IsNullOrWhiteSpace(appId)
                ? await workflowQueryPort.GetByWorkflowIdAsync(scopeId, workflowId, ct)
                : await workflowQueryPort.GetByWorkflowIdAsync(scopeId, appId, workflowId, ct);
            if (workflow == null)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    "USER_WORKFLOW_NOT_FOUND",
                    BuildWorkflowNotFoundMessage(scopeId, workflowId, appId),
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

    private static async Task HandleRunWorkflowStreamAsyncCore(
        HttpContext http,
        string scopeId,
        string? appId,
        RunScopeWorkflowStreamHttpRequest request,
        IScopeWorkflowQueryPort workflowQueryPort,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
    {
        try
        {
            if (await TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var workflow = string.IsNullOrWhiteSpace(appId)
                ? await workflowQueryPort.GetByActorIdAsync(scopeId, request.ActorId, ct)
                : await workflowQueryPort.GetByActorIdAsync(scopeId, appId, request.ActorId, ct);
            if (workflow == null)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    "USER_WORKFLOW_NOT_FOUND",
                    BuildWorkflowActorNotFoundMessage(scopeId, appId),
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

    private static async Task<IResult> HandleStopWorkflowRunAsyncCore(
        HttpContext http,
        string scopeId,
        string? appId,
        StopScopeWorkflowRunHttpRequest request,
        IScopeWorkflowQueryPort workflowQueryPort,
        IWorkflowActorBindingReader workflowActorBindingReader,
        ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        CancellationToken ct)
    {
        try
        {
            if (TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var actorId = NormalizeRequired(request.ActorId, nameof(request.ActorId));
            _ = NormalizeRequired(request.RunId, nameof(request.RunId));

            var binding = await workflowActorBindingReader.GetAsync(actorId, ct);
            if (binding?.ActorKind != WorkflowActorKind.Run ||
                string.IsNullOrWhiteSpace(binding.EffectiveDefinitionActorId))
            {
                return Results.NotFound(new
                {
                    code = "USER_WORKFLOW_NOT_FOUND",
                    message = BuildWorkflowActorNotFoundMessage(scopeId, appId),
                });
            }

            if (!string.IsNullOrWhiteSpace(binding.ScopeId) &&
                !string.Equals(binding.ScopeId, scopeId, StringComparison.Ordinal))
            {
                return Results.NotFound(new
                {
                    code = "USER_WORKFLOW_NOT_FOUND",
                    message = BuildWorkflowActorNotFoundMessage(scopeId, appId),
                });
            }

            var workflow = string.IsNullOrWhiteSpace(appId)
                ? await workflowQueryPort.GetByActorIdAsync(scopeId, binding.EffectiveDefinitionActorId, ct)
                : await workflowQueryPort.GetByActorIdAsync(scopeId, appId, binding.EffectiveDefinitionActorId, ct);
            if (workflow == null)
            {
                return Results.NotFound(new
                {
                    code = "USER_WORKFLOW_NOT_FOUND",
                    message = BuildWorkflowActorNotFoundMessage(scopeId, appId),
                });
            }

            return await WorkflowCapabilityEndpoints.HandleStop(
                new WorkflowStopInput
                {
                    ActorId = actorId,
                    RunId = request.RunId,
                    CommandId = request.CommandId,
                    Reason = request.Reason,
                },
                stopService,
                ct);
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

    private static async Task HandleRunWorkflowStreamCoreAsync(
        HttpContext http,
        string scopeId,
        ScopeWorkflowSummary workflow,
        string prompt,
        string? sessionId,
        Dictionary<string, string>? headers,
        string? eventFormat,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
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
            await WorkflowCapabilityEndpoints.HandleChat(
                http,
                new ChatInput
                {
                    Prompt = prompt,
                    AgentId = workflow.ActorId,
                    SessionId = sessionId,
                    ScopeId = NormalizeRequired(scopeId, nameof(scopeId)),
                    Metadata = BuildScopedHeaders(scopeId, headers),
                },
                chatRunService,
                ct);
            return;
        }

        await HandleAguiStreamAsync(
            http,
            scopeId,
            workflow,
            prompt,
            sessionId,
            BuildScopedHeaders(scopeId, headers),
            chatRunService,
            ct);
    }

    private static async Task HandleAguiStreamAsync(
        HttpContext http,
        string scopeId,
        ScopeWorkflowSummary workflow,
        string prompt,
        string? sessionId,
        IReadOnlyDictionary<string, string>? headers,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
    {
        prompt = string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt.Trim();

        var started = false;

        async Task StartAsync(CancellationToken token)
        {
            if (started)
                return;

            started = true;
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.StartAsync(token);
        }

        await using var writer = new AGUISseWriter(http.Response, ScopeWorkflowAguiEventMapper.TypeRegistry);

        try
        {
            var result = await chatRunService.ExecuteAsync(
                new WorkflowChatRunRequest(
                    prompt,
                    workflow.WorkflowName,
                    workflow.ActorId,
                    sessionId,
                    WorkflowYamls: null,
                    Metadata: BuildScopedHeaders(scopeId, headers),
                    ScopeId: NormalizeRequired(scopeId, nameof(scopeId))),
                async (frame, token) =>
                {
                    if (!ScopeWorkflowAguiEventMapper.TryMap(frame, out var aguiEvent) || aguiEvent == null)
                        return;

                    await StartAsync(token);
                    await writer.WriteAsync(aguiEvent, token);
                },
                async (receipt, token) =>
                {
                    if (!string.IsNullOrWhiteSpace(receipt.CorrelationId))
                        http.Response.Headers["X-Correlation-Id"] = receipt.CorrelationId;

                    await StartAsync(token);
                    await writer.WriteAsync(ScopeWorkflowAguiEventMapper.BuildRunContextEvent(receipt), token);
                },
                ct);

            if (!result.Succeeded && !started)
            {
                var (statusCode, code, message) = MapRunStartError(result.Error);
                await WriteJsonErrorResponseAsync(http, statusCode, code, message, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!started)
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

    private static async Task<ScopeWorkflowDetail> BuildWorkflowDetailAsync(
        ScopeWorkflowSummary workflow,
        IWorkflowActorBindingReader workflowActorBindingReader,
        IServiceRevisionArtifactStore? artifactStore,
        CancellationToken ct)
    {
        PreparedServiceRevisionArtifact? artifact = null;
        WorkflowActorBinding? binding = null;
        if (!string.IsNullOrWhiteSpace(workflow.ActorId))
            binding = await workflowActorBindingReader.GetAsync(workflow.ActorId, ct);

        if (artifactStore != null &&
            !string.IsNullOrWhiteSpace(workflow.ServiceKey) &&
            !string.IsNullOrWhiteSpace(workflow.ActiveRevisionId))
        {
            artifact = await artifactStore.GetAsync(workflow.ServiceKey, workflow.ActiveRevisionId, ct);
        }

        return BuildWorkflowDetailPayload(workflow, binding, artifact);
    }

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
            workflow.AppId,
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
        string workflowId,
        string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return $"Workflow '{workflowId}' was not found for scope '{scopeId}'.";

        return $"Workflow '{workflowId}' was not found for scope '{scopeId}' and app '{appId.Trim()}'.";
    }

    private static string BuildWorkflowActorNotFoundMessage(string scopeId, string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return "Workflow actor was not found for the specified scope.";

        return $"Workflow actor was not found for scope '{scopeId}' and app '{appId.Trim()}'.";
    }

    private static bool TryParseEventFormat(
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
        string scopeId,
        IReadOnlyDictionary<string, string>? headers)
    {
        var scopedHeaders = headers == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        scopedHeaders.Remove("scope_id");
        scopedHeaders.Remove(WorkflowRunCommandMetadataKeys.ScopeId);
        return scopedHeaders;
    }

    private static (int StatusCode, string Code, string Message) MapRunStartError(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.AgentNotFound => (StatusCodes.Status404NotFound, "AGENT_NOT_FOUND", "Agent not found."),
            WorkflowChatRunStartError.WorkflowNotFound => (StatusCodes.Status404NotFound, "WORKFLOW_NOT_FOUND", "Workflow not found."),
            WorkflowChatRunStartError.AgentTypeNotSupported => (StatusCodes.Status400BadRequest, "AGENT_TYPE_NOT_SUPPORTED", "Actor is not workflow-capable."),
            WorkflowChatRunStartError.ProjectionDisabled => (StatusCodes.Status503ServiceUnavailable, "PROJECTION_DISABLED", "Projection pipeline is disabled."),
            WorkflowChatRunStartError.WorkflowBindingMismatch => (StatusCodes.Status409Conflict, "WORKFLOW_BINDING_MISMATCH", "Actor is bound to a different workflow."),
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => (StatusCodes.Status409Conflict, "AGENT_WORKFLOW_NOT_CONFIGURED", "Actor has no bound workflow."),
            WorkflowChatRunStartError.InvalidWorkflowYaml => (StatusCodes.Status400BadRequest, "INVALID_WORKFLOW_YAML", "Workflow YAML is invalid."),
            WorkflowChatRunStartError.WorkflowNameMismatch => (StatusCodes.Status400BadRequest, "WORKFLOW_NAME_MISMATCH", "Workflow name does not match workflow YAML."),
            WorkflowChatRunStartError.PromptRequired => (StatusCodes.Status400BadRequest, "PROMPT_REQUIRED", "Prompt is required."),
            WorkflowChatRunStartError.ConflictingScopeId => (StatusCodes.Status400BadRequest, "CONFLICTING_SCOPE_ID", "Conflicting scope_id values were provided."),
            _ => (StatusCodes.Status400BadRequest, "RUN_START_FAILED", "Failed to resolve actor."),
        };
    }

    private static bool TryCreateScopeAccessDeniedResult(
        HttpContext http,
        string scopeId,
        out IResult denied)
    {
        if (!TryGetAuthenticatedScopeGuardFailure(http, scopeId, out var message))
        {
            denied = Results.Empty;
            return false;
        }

        denied = Results.Json(
            new
            {
                code = "SCOPE_ACCESS_DENIED",
                message,
            },
            statusCode: StatusCodes.Status403Forbidden);
        return true;
    }

    private static async Task<bool> TryWriteScopeAccessDeniedAsync(
        HttpContext http,
        string scopeId,
        CancellationToken ct)
    {
        if (!TryGetAuthenticatedScopeGuardFailure(http, scopeId, out var message))
            return false;

        await WriteJsonErrorResponseAsync(
            http,
            StatusCodes.Status403Forbidden,
            "SCOPE_ACCESS_DENIED",
            message,
            ct);
        return true;
    }

    private static bool TryGetAuthenticatedScopeGuardFailure(
        HttpContext http,
        string requestedScopeId,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(http);

        message = string.Empty;
        if (http.User?.Identity?.IsAuthenticated != true)
            return false;

        var normalizedRequestedScopeId = NormalizeRequired(requestedScopeId, nameof(requestedScopeId));
        var claimedScopeIds = http.User.Claims
            .Where(static claim => ScopeClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            .Select(static claim => claim.Value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (claimedScopeIds.Count == 0)
            return false;

        if (claimedScopeIds.Count > 1)
        {
            message = "Authenticated scope is ambiguous.";
            return true;
        }

        if (string.Equals(claimedScopeIds[0], normalizedRequestedScopeId, StringComparison.Ordinal))
            return false;

        message = "Authenticated scope does not match requested scope.";
        return true;
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
        string? RevisionId = null);

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

    public sealed record StopScopeWorkflowRunHttpRequest(
        string ActorId,
        string RunId,
        string? CommandId = null,
        string? Reason = null);

    private enum ScopeWorkflowStreamEventFormat
    {
        Workflow = 0,
        Agui = 1,
    }
}
