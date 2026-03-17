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

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ScopeWorkflowEndpoints
{
    public static IEndpointRouteBuilder MapScopeWorkflowCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("ScopeWorkflows");
        group.MapPut("/{scopeId}/workflows/{workflowId}", HandleUpsertWorkflowAsync)
            .Produces<ScopeWorkflowUpsertResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapGet("/{scopeId}/workflows", HandleListWorkflowsAsync)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapGet("/{scopeId}/workflows/{workflowId}", HandleGetWorkflowDetailAsync)
            .Produces<ScopeWorkflowDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/workflows/{workflowId}/runs:stream", HandleRunWorkflowByIdStreamAsync)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/workflow-runs:stream", HandleRunWorkflowStreamAsync)
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{scopeId}/workflow-runs/stop", HandleStopWorkflowRunAsync)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        return app;
    }

    internal static async Task<IResult> HandleUpsertWorkflowAsync(
        string scopeId,
        string workflowId,
        UpsertScopeWorkflowHttpRequest request,
        [FromServices] IScopeWorkflowCommandPort workflowCommandPort,
        CancellationToken ct)
    {
        try
        {
            var result = await workflowCommandPort.UpsertAsync(new ScopeWorkflowUpsertRequest(
                scopeId,
                workflowId,
                request.WorkflowYaml,
                request.WorkflowName,
                request.DisplayName,
                request.InlineWorkflowYamls,
                request.RevisionId), ct);
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

    internal static async Task<IResult> HandleListWorkflowsAsync(
        string scopeId,
        bool includeSource,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] IServiceRevisionArtifactStore? artifactStore,
        CancellationToken ct)
    {
        try
        {
            var workflows = await workflowQueryPort.ListAsync(scopeId, ct);
            if (!includeSource)
                return Results.Ok(workflows);

            var details = new List<ScopeWorkflowDetail>(workflows.Count);
            foreach (var workflow in workflows)
                details.Add(await BuildWorkflowDetailAsync(scopeId, workflow, workflowActorBindingReader, artifactStore, ct));

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

    internal static async Task<IResult> HandleGetWorkflowDetailAsync(
        string scopeId,
        string workflowId,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] IServiceRevisionArtifactStore? artifactStore,
        CancellationToken ct)
    {
        try
        {
            var workflow = await workflowQueryPort.GetByWorkflowIdAsync(scopeId, workflowId, ct);
            if (workflow == null)
            {
                return Results.NotFound(new
                {
                    code = "USER_WORKFLOW_NOT_FOUND",
                    message = $"Workflow '{workflowId}' was not found for scope '{scopeId}'.",
                });
            }

            return Results.Json(await BuildWorkflowDetailAsync(scopeId, workflow, workflowActorBindingReader, artifactStore, ct));
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

    internal static async Task HandleRunWorkflowByIdStreamAsync(
        HttpContext http,
        string scopeId,
        string workflowId,
        RunScopeWorkflowByIdStreamHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
    {
        try
        {
            var workflow = await workflowQueryPort.GetByWorkflowIdAsync(scopeId, workflowId, ct);
            if (workflow == null)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    "USER_WORKFLOW_NOT_FOUND",
                    $"Workflow '{workflowId}' was not found for scope '{scopeId}'.",
                    ct);
                return;
            }

            await HandleRunWorkflowStreamCoreAsync(
                http,
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

    internal static async Task HandleRunWorkflowStreamAsync(
        HttpContext http,
        string scopeId,
        RunScopeWorkflowStreamHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
    {
        try
        {
            var workflow = await workflowQueryPort.GetByActorIdAsync(scopeId, request.ActorId, ct);
            if (workflow == null)
            {
                await WriteJsonErrorResponseAsync(
                    http,
                    StatusCodes.Status404NotFound,
                    "USER_WORKFLOW_NOT_FOUND",
                    "Workflow actor was not found for the specified scope.",
                    ct);
                return;
            }

            await HandleRunWorkflowStreamCoreAsync(
                http,
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

    internal static async Task<IResult> HandleStopWorkflowRunAsync(
        string scopeId,
        StopScopeWorkflowRunHttpRequest request,
        [FromServices] IScopeWorkflowQueryPort workflowQueryPort,
        [FromServices] IWorkflowActorBindingReader workflowActorBindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        CancellationToken ct)
    {
        try
        {
            var actorId = NormalizeRequired(request.ActorId, nameof(request.ActorId));
            _ = NormalizeRequired(request.RunId, nameof(request.RunId));

            var binding = await workflowActorBindingReader.GetAsync(actorId, ct);
            if (binding?.ActorKind != WorkflowActorKind.Run ||
                string.IsNullOrWhiteSpace(binding.EffectiveDefinitionActorId))
            {
                return Results.NotFound(new
                {
                    code = "USER_WORKFLOW_NOT_FOUND",
                    message = "Workflow run actor was not found for the specified scope.",
                });
            }

            var workflow = await workflowQueryPort.GetByActorIdAsync(scopeId, binding.EffectiveDefinitionActorId, ct);
            if (workflow == null)
            {
                return Results.NotFound(new
                {
                    code = "USER_WORKFLOW_NOT_FOUND",
                    message = "Workflow run actor was not found for the specified scope.",
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
                    Metadata = headers == null
                        ? null
                        : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
                },
                chatRunService,
                ct);
            return;
        }

        await HandleAguiStreamAsync(
            http,
            workflow,
            prompt,
            sessionId,
            headers,
            chatRunService,
            ct);
    }

    private static async Task HandleAguiStreamAsync(
        HttpContext http,
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
                    Metadata: headers == null
                        ? null
                        : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)),
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
        string scopeId,
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

        return BuildWorkflowDetailPayload(scopeId, workflow, binding, artifact);
    }

    private static ScopeWorkflowDetail BuildWorkflowDetailPayload(
        string scopeId,
        ScopeWorkflowSummary workflow,
        WorkflowActorBinding? binding,
        PreparedServiceRevisionArtifact? artifact)
    {
        var workflowPlan = artifact?.DeploymentPlan?.WorkflowPlan;
        var hasBindingSource = binding?.HasDefinitionPayload == true;
        return new ScopeWorkflowDetail(
            true,
            scopeId,
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

    private static (int StatusCode, string Code, string Message) MapRunStartError(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.AgentNotFound => (StatusCodes.Status404NotFound, "AGENT_NOT_FOUND", "Agent not found."),
            WorkflowChatRunStartError.WorkflowNotFound => (StatusCodes.Status404NotFound, "WORKFLOW_NOT_FOUND", "Workflow not found."),
            WorkflowChatRunStartError.AgentTypeNotSupported => (StatusCodes.Status400BadRequest, "AGENT_TYPE_NOT_SUPPORTED", "Actor is not workflow-capable."),
            WorkflowChatRunStartError.ProjectionDisabled => (StatusCodes.Status503ServiceUnavailable, "PROJECTION_DISABLED", "Projection pipeline is disabled."),
            WorkflowChatRunStartError.DetachedCleanupUnavailable => (StatusCodes.Status503ServiceUnavailable, "DETACHED_CLEANUP_UNAVAILABLE", "Detached cleanup pipeline is unavailable."),
            WorkflowChatRunStartError.WorkflowBindingMismatch => (StatusCodes.Status409Conflict, "WORKFLOW_BINDING_MISMATCH", "Actor is bound to a different workflow."),
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => (StatusCodes.Status409Conflict, "AGENT_WORKFLOW_NOT_CONFIGURED", "Actor has no bound workflow."),
            WorkflowChatRunStartError.InvalidWorkflowYaml => (StatusCodes.Status400BadRequest, "INVALID_WORKFLOW_YAML", "Workflow YAML is invalid."),
            WorkflowChatRunStartError.WorkflowNameMismatch => (StatusCodes.Status400BadRequest, "WORKFLOW_NAME_MISMATCH", "Workflow name does not match workflow YAML."),
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
