using System.Security.Claims;
using Aevatar.BackendConsole.Hosting;
using Aevatar.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Skills;

// 06-26 ornn skills invocation page (sibling of the workflow run observatory). Data endpoints are bearer +
// RequireAuthorization; the caller's own NyxID access token (the bearer) scopes skill visibility, so a caller
// only ever lists skills they could invoke. Invoke / schedule actions are added in later stages.
internal static class WorkflowSkillsEndpoints
{
    private const string PageRoute = "/workflow/skills";
    private const string DataRoutePrefix = "/api/workflow/skills";

    private static readonly BackendConsoleAsset PageAsset = new(
        LogicalName: "workflow-skills",
        Assembly: typeof(WorkflowSkillsEndpoints).Assembly,
        ResourceSuffix: "Skills.workflow-skills.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    public static IEndpointRouteBuilder MapWorkflowSkillsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetSkillsPage)
            .WithTags("WorkflowSkills")
            .WithName("GetWorkflowSkillsPage")
            .WithSummary("ornn skills invocation page served from an embedded static asset.")
            .AllowAnonymous();

        var data = app.MapGroup(DataRoutePrefix).WithTags("WorkflowSkills");

        data.MapGet(string.Empty, ListSkills)
            .WithName("ListWorkflowSkills")
            .WithSummary("List the caller's invocable ornn skills (scoped to the caller's NyxID identity).")
            .RequireAuthorization();

        data.MapGet("/{guid}", GetSkill)
            .WithName("GetWorkflowSkill")
            .WithSummary("Skill detail (authoritative runKind + whenToUse) resolved on selection.")
            .RequireAuthorization();

        data.MapPost("/{guid}/invoke", InvokeSkill)
            .WithName("InvokeWorkflowSkill")
            .WithSummary("Invoke a skill once as a workflow run; returns the run id for the observatory.")
            .RequireAuthorization();

        data.MapPost("/{guid}/schedule", ScheduleSkill)
            .WithName("ScheduleWorkflowSkill")
            .WithSummary("Provision a recurring (cron) schedule for a skill; runs surface in the observatory.")
            .RequireAuthorization();

        return app;
    }

    internal static IResult GetSkillsPage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(PageAsset);
    }

    internal static async Task<IResult> ListSkills(
        HttpContext http,
        [FromServices] IUserSkillCatalogQueryService catalog,
        string? query = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(catalog);

        // RequireAuthorization guarantees an authenticated principal; the bearer it carries is the caller's
        // NyxID access token, which the Ornn proxy uses to scope visibility. Read it directly (NyxID tokens
        // are opaque api keys, not necessarily JWT claims).
        if (!TryGetBearerToken(http, out var token))
            return Results.Unauthorized();

        var result = await catalog.ListVisibleSkillsAsync(token, query ?? string.Empty, page, pageSize, ct);
        return result.Error is not null
            ? Results.Json(result, statusCode: StatusCodes.Status502BadGateway)
            : Results.Json(result);
    }

    internal static async Task<IResult> GetSkill(
        HttpContext http,
        string guid,
        [FromServices] IUserSkillCatalogQueryService catalog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!TryGetBearerToken(http, out var token))
            return Results.Unauthorized();

        var detail = await catalog.GetSkillAsync(token, guid, ct);
        return detail is null ? Results.NotFound() : Results.Json(detail);
    }

    internal static async Task<IResult> InvokeSkill(
        HttpContext http,
        string guid,
        [FromServices] IUserSkillRunService runService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(runService);

        if (!TryGetBearerToken(http, out var token))
            return Results.Unauthorized();
        // The run is attributed to the caller's scope so it surfaces in their observatory.
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        SkillInvokeRequest body;
        try
        {
            body = await http.Request.ReadFromJsonAsync<SkillInvokeRequest>(ct) ?? new SkillInvokeRequest();
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.BadRequest(new { error = "invalid_json" });
        }

        var outcome = await runService.InvokeOnceAsync(guid, token, scopeId, body.Prompt ?? string.Empty, ct);
        if (outcome.Succeeded)
            return Results.Json(outcome.Receipt);

        var statusCode = string.Equals(outcome.ErrorCode, "skill_not_found", StringComparison.Ordinal)
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status502BadGateway;
        return Results.Json(new { code = outcome.ErrorCode, message = outcome.ErrorMessage }, statusCode: statusCode);
    }

    internal static async Task<IResult> ScheduleSkill(
        HttpContext http,
        string guid,
        [FromServices] IUserSkillRunService runService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(runService);

        if (!TryGetBearerToken(http, out var token))
            return Results.Unauthorized();
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        SkillScheduleHttpRequest body;
        try
        {
            body = await http.Request.ReadFromJsonAsync<SkillScheduleHttpRequest>(ct) ?? new SkillScheduleHttpRequest();
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.BadRequest(new { error = "invalid_json" });
        }

        if (string.IsNullOrWhiteSpace(body.CronExpression))
            return Results.BadRequest(new { code = "cron_required", message = "cronExpression is required." });
        if (string.IsNullOrWhiteSpace(body.TeamId))
            return Results.BadRequest(new { code = "team_id_required", message = "teamId is required." });

        var outcome = await runService.ScheduleAsync(
            guid,
            token,
            scopeId,
            ResolveOwnerSubject(http),
            body.Prompt ?? string.Empty,
            body.CronExpression!,
            body.Timezone ?? string.Empty,
            body.DisplayName ?? string.Empty,
            body.TeamId!,
            ct);
        if (outcome.Succeeded)
            return Results.Json(outcome.Receipt);

        var scheduleStatus = string.Equals(outcome.ErrorCode, "skill_not_found", StringComparison.Ordinal)
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status502BadGateway;
        return Results.Json(new { code = outcome.ErrorCode, message = outcome.ErrorMessage }, statusCode: scheduleStatus);
    }

    // The caller's NyxID subject (uid/sub claim) is the schedule's owner subject; the provisioning adapter
    // substitutes the scope id when this is absent.
    private static string? ResolveOwnerSubject(HttpContext http)
    {
        foreach (var claimType in new[] { "uid", "sub", ClaimTypes.NameIdentifier, "user_id" })
        {
            var value = http.User.FindFirst(claimType)?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool TryGetBearerToken(HttpContext http, out string token)
    {
        token = string.Empty;
        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = header[prefix.Length..].Trim();
        if (value.Length == 0)
            return false;

        token = value;
        return true;
    }
}
