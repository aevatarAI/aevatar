using Aevatar.Capabilities;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using static Aevatar.GAgentService.Hosting.Endpoints.ScopeWorkflowEndpoints;

namespace Aevatar.GAgentService.Hosting.Endpoints;

/// <summary>Current-scope HTTP mappings over existing Workflow and Activity capabilities.</summary>
public static class WorkflowApiEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowApi(this IEndpointRouteBuilder app)
    {
        var workflows = app.MapGroup("/api/v1/workflows").WithTags("Workflows").RequireAuthorization();
        Describe(workflows.MapGet("/catalogue", Catalogue), "list_workflows", true,
            "List Workflow catalogue entries.", "Current scope only. Supports all, drafts, or archived views with existing search and pagination; read models are eventually consistent.")
            .Produces<ScopeWorkflowCatalogueResponse>();
        Describe(workflows.MapGet("/drafts/{workflowId}", GetDraft), "get_workflow_draft", true,
            "Read a Workflow Draft.", "Read server-visible YAML and name after saving. A temporary 404 does not prove a preceding accepted command failed.")
            .Produces<WorkflowDraftResponse>().Produces(StatusCodes.Status404NotFound);
        Describe(workflows.MapPost("/drafts", CreateDraft), "create_workflow_draft", false,
            "Create a Workflow Draft.", "202 accepts the existing save command. Read back by workflowId; do not retry side effects automatically. Scoped layout persistence is not supported.")
            .Produces<WorkflowDraftAcceptedResponse>(StatusCodes.Status202Accepted).Produces(StatusCodes.Status409Conflict);
        Describe(workflows.MapPut("/drafts/{workflowId}", SaveDraft), "save_workflow_draft", false,
            "Save a known Workflow Draft.", "Requires an existing Draft or unambiguous committed published Workflow in the current scope. Restores source to the same workflowId. No version or concurrency guarantee; layout is not persisted. Read back after 202.")
            .Produces<WorkflowDraftAcceptedResponse>(StatusCodes.Status202Accepted).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
        Describe(workflows.MapPost("/publication-preview", Preview), "preview_workflow_external_requests", true,
            "Preview external requests in Workflow YAML.", "Preview the exact YAML and inline YAML that will be published. Reuse existing confirmation inputs; this does not lock a publication snapshot.")
            .Produces<ExplicitRequestPreviewHttpResult>();
        Describe(workflows.MapPut("/{workflowId}", Publish), "publish_workflow", false,
            "Publish a Workflow revision.", "Publishes the supplied YAML, not a Draft save. 202 preserves every original stage command handle. Read published details and confirm this receipt's target revision before invoking; an older runnable revision is insufficient. Do not automatically retry.")
            .Produces<WorkflowPublicationAcceptedResponse>(StatusCodes.Status202Accepted);
        Describe(workflows.MapGet("/{workflowId}", GetPublished), "get_published_workflow", true,
            "Read a published Workflow and its source.", "Uses existing runnable-resource lookup. Preserve not-found, not-ready, and stale distinctions; compare activeRevisionId with the publication target.")
            .Produces<ScopeWorkflowDetail>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
        Describe(workflows.MapPost("/{workflowId}/invoke", Invoke), "invoke_workflow", false,
            "Invoke a published Workflow with text.", "Returns a finite JSON 202 receipt from actual dispatch. Query Activity using the actual runId, never commandId or an inferred actor address. Optional revisionId must be the confirmed target. No automatic side-effect retries or new idempotency guarantee.")
            .Produces<WorkflowInvocationAcceptedResponse>(StatusCodes.Status202Accepted).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);

        var activity = app.MapGroup("/api/v1/activity-runs").WithTags("Activity").RequireAuthorization();
        Describe(activity.MapGet("", ListActivity), "list_activity_runs", true,
            "List Activity runs in the current scope.", "Uses existing pagination, status and workflowId filters. No definition or caller-selected scope filter.")
            .Produces<WorkflowActivityRunFeedPage>();
        Describe(activity.MapGet("/{runId}", GetActivity), "get_activity_run", true,
            "Read an Activity run.", "Use the actual runId returned by invocation. Existing input, output, step, error, usage and version facts are returned; a missing read model can require bounded readback.")
            .Produces<ObservatoryRunDetail>().Produces(StatusCodes.Status404NotFound);
        return app;
    }

    private static RouteHandlerBuilder Describe(RouteHandlerBuilder endpoint, string operationId, bool readOnly, string summary, string description) =>
        endpoint.WithName(operationId).WithSummary(summary).WithDescription(description)
            .WithMetadata(new AevatarToolMetadata(readOnly))
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status400BadRequest);

    private static async Task<IResult> Catalogue(HttpContext http, [FromServices] IAppScopedWorkflowCatalogueService catalogue,
        string? view, string? query, string? cursor, int? take, CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId)) return Results.Unauthorized();
        return await HandleQueryWorkflowCatalogueAsync(http, scopeId, view, query, cursor, take, catalogue, ct);
    }

    private static async Task<IResult> GetDraft(HttpContext http, string workflowId, [FromServices] AppScopedWorkflowService drafts, CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId)) return Results.Unauthorized();
        try
        {
            var draft = await drafts.GetDraftAsync(scopeId, workflowId, ct);
            return draft == null ? Results.NotFound() : Results.Ok(draft);
        }
        catch (AppApiException ex) { return Results.Json(AppApiErrors.CreatePayload(ex), statusCode: ex.StatusCode); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
    }

    private static Task<IResult> CreateDraft(HttpContext http, WorkflowDraftWriteRequest request,
        [FromServices] AppScopedWorkflowService drafts, CancellationToken ct) => WriteDraft(http, null, request, drafts, ct);

    private static Task<IResult> SaveDraft(HttpContext http, string workflowId, WorkflowDraftWriteRequest request,
        [FromServices] AppScopedWorkflowService drafts, CancellationToken ct) => WriteDraft(http, workflowId, request, drafts, ct);

    private static async Task<IResult> WriteDraft(HttpContext http, string? workflowId, WorkflowDraftWriteRequest request,
        AppScopedWorkflowService drafts, CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId)) return Results.Unauthorized();
        try
        {
            var save = new SaveWorkflowDraftRequest(string.Empty, request.WorkflowName, request.FileName, request.Yaml, request.Layout);
            var receipt = workflowId == null
                ? await drafts.CreateDraftAsync(scopeId, save, ct)
                : await drafts.SaveKnownWorkflowDraftAsync(scopeId, workflowId, save, ct);
            return Results.Accepted(value: new WorkflowDraftAcceptedResponse(receipt.WorkflowId, receipt.CommandId));
        }
        catch (WorkflowDraftNotFoundException) { return Results.NotFound(); }
        catch (WorkflowDraftPathConflictException ex)
        {
            return Results.Conflict(new { code = "WORKFLOW_DRAFT_PATH_CONFLICT", message = ex.Message,
                workflowId = ex.WorkflowId, targetPath = ex.TargetPath, conflictingWorkflowId = ex.ConflictingWorkflowId });
        }
        catch (AppApiException ex) { return Results.Json(AppApiErrors.CreatePayload(ex), statusCode: ex.StatusCode); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
    }

    private static async Task<IResult> Preview(HttpContext http, ExplicitRequestPreviewHttpRequest request,
        [FromServices] IWorkflowExplicitRequestPreviewService preview, CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId)) return Results.Unauthorized();
        return await HandleExplicitRequestPreviewAsync(http, scopeId, request, preview, ct);
    }

    private static async Task<IResult> Publish(HttpContext http, string workflowId, UpsertScopeWorkflowHttpRequest request,
        [FromServices] IScopeWorkflowCommandPort commands, CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId)) return Results.Unauthorized();
        var result = await HandleUpsertWorkflowAsync(http, scopeId, workflowId, request, commands, ct);
        if (result is IValueHttpResult { Value: ScopeWorkflowUpsertResult receipt })
            return Results.Accepted(value: new WorkflowPublicationAcceptedResponse(receipt.WorkflowId, receipt.RevisionId,
                receipt.AcceptedAtUtc, receipt.CommandHandles.Select(handle => new WorkflowPublicationCommandHandle(
                    handle.Stage, handle.CommandId, handle.CorrelationId)).ToArray()));
        return result;
    }

    private static async Task<IResult> GetPublished(HttpContext http, string workflowId,
        [FromServices] IScopeWorkflowQueryPort query, [FromServices] IWorkflowActorBindingReader bindingReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionReader, [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId)) return Results.Unauthorized();
        return await HandleGetWorkflowDetailAsync(http, scopeId, workflowId, query, bindingReader, revisionReader, options, ct);
    }

    private static async Task<IResult> Invoke(HttpContext http, string workflowId, WorkflowTextInvocationHttpRequest request,
        [FromServices] IScopeWorkflowTextInvocationPort invocation, [FromServices] ServiceInvokeReadinessErrorMapper readinessErrors,
        CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId)) return Results.Unauthorized();
        var extraction = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct);
        if (!extraction.Succeeded)
        {
            var (statusCode, code, message) = MapRunStartError(extraction.Error);
            return Results.Json(new { code, message }, statusCode: statusCode);
        }
        try
        {
            var receipt = await invocation.InvokeAsync(new ScopeWorkflowTextInvocationRequest(scopeId, workflowId,
                request.Prompt, request.RevisionId, request.CommandId, request.CorrelationId), extraction.Credential, ct);
            return Results.Accepted(value: new WorkflowInvocationAcceptedResponse(receipt.RunId, receipt.CommandId, receipt.CorrelationId));
        }
        catch (ScopeWorkflowInvocationLookupException ex)
        {
            var (statusCode, code, message) = MapWorkflowLookupError(scopeId, workflowId, ex.Lookup);
            return Results.Json(new { code, message }, statusCode: statusCode);
        }
        catch (ServiceInvokeReadinessException ex) { return Results.BadRequest(readinessErrors.Map(ex)); }
        catch (ArgumentException ex) { return Results.BadRequest(new { code = "INVALID_USER_WORKFLOW_REQUEST", message = ex.Message }); }
        catch (InvalidOperationException ex) { return ScopeServiceEndpoints.CreateScopeInvokeFailureResult(ex); }
    }

    private static async Task<IResult> ListActivity(HttpContext http, [FromServices] IWorkflowRunObservatoryQueryService observatory,
        string? status, string? origin, string? schedule, string? workflowId, string? q, string? from, string? to,
        int? take, string? cursor, bool? includeTotalCount, CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId)) return Results.Unauthorized();
        try
        {
            return Results.Ok(await observatory.ListActivityRunsForScopeAsync(scopeId, new WorkflowActivityRunFeedFilter
            {
                Status = status, Origins = SplitCsv(origin), ScheduleIds = SplitCsv(schedule), WorkflowId = workflowId,
                SearchText = q, FromUtc = ParseTimestamp(from), ToUtc = ParseTimestamp(to), Take = take ?? 100,
                Cursor = cursor, IncludeTotalCount = includeTotalCount ?? false,
            }, ct));
        }
        catch (ProjectionDocumentQueryCursorException) when (!string.IsNullOrWhiteSpace(cursor))
        { return Results.BadRequest(new { error = "malformed_cursor" }); }
    }

    private static async Task<IResult> GetActivity(HttpContext http, string runId,
        [FromServices] IWorkflowRunObservatoryQueryService observatory, CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId)) return Results.Unauthorized();
        var detail = await observatory.GetRunForScopeAsync(scopeId, runId, ct);
        return detail == null ? Results.NotFound() : Results.Ok(detail);
    }

    private static IReadOnlyList<string> SplitCsv(string? value) => string.IsNullOrWhiteSpace(value)
        ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static DateTimeOffset? ParseTimestamp(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    public sealed record WorkflowDraftWriteRequest(string WorkflowName, string Yaml, string? FileName = null, WorkflowLayoutDocument? Layout = null);
    public sealed record WorkflowDraftAcceptedResponse(string WorkflowId, string CommandId);
    public sealed record WorkflowPublicationAcceptedResponse(string WorkflowId, string RevisionId, DateTimeOffset AcceptedAtUtc,
        IReadOnlyList<WorkflowPublicationCommandHandle> CommandHandles);
    public sealed record WorkflowPublicationCommandHandle(string Stage, string CommandId, string CorrelationId);
    public sealed record WorkflowTextInvocationHttpRequest(string Prompt, string? RevisionId = null, string? CommandId = null, string? CorrelationId = null);
    public sealed record WorkflowInvocationAcceptedResponse(string RunId, string CommandId, string CorrelationId);
}
