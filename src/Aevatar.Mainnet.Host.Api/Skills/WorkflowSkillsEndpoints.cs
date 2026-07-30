using Aevatar.BackendConsole.Hosting;
using Aevatar.Capabilities;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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

        data.MapGet("/{guid}/exact", GetExactSkill)
            .WithName("GetWorkflowExactSkill")
            .WithSummary("Exact Ornn authority fields for an Agent Profile skill reference.")
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

    internal static async Task<IResult> GetExactSkill(
        HttpContext http,
        string guid,
        [FromServices] IUserSkillCatalogQueryService catalog,
        string? literalVersion = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!TryGetBearerToken(http, out var token))
            return Results.Unauthorized();
        if (!Guid.TryParseExact(guid, "D", out var parsedGuid) ||
            !string.Equals(parsedGuid.ToString("D"), guid, StringComparison.Ordinal))
        {
            return Results.BadRequest(new AgentProfileExactSkillError(
                "invalid_guid",
                "guid must be a canonical lowercase UUID."));
        }
        if (literalVersion is not null && !IsLiteralVersion(literalVersion))
        {
            return Results.BadRequest(new AgentProfileExactSkillError(
                "invalid_literal_version",
                "literalVersion must use canonical major.minor form."));
        }

        var read = await catalog.GetExactSkillAsync(token, guid, literalVersion, ct);
        if (read.Detail is not null)
            return Results.Json(read.Detail);
        if (read.UpstreamStatus == StatusCodes.Status403Forbidden)
            return Results.Forbid();
        if (string.Equals(read.Error, "exact_skill_not_found", StringComparison.Ordinal))
        {
            return Results.NotFound(new AgentProfileExactSkillError(
                "exact_skill_not_found",
                "The requested exact skill was not found."));
        }

        return Results.Json(
            new AgentProfileExactSkillError(
                read.Error ?? "exact_skill_upstream_failure",
                "The exact skill authority could not be resolved."),
            statusCode: StatusCodes.Status502BadGateway);
    }

    internal static bool IsLiteralVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Split('.', StringSplitOptions.None) is not [var major, var minor] ||
            !int.TryParse(major, out var majorValue) ||
            !int.TryParse(minor, out var minorValue) ||
            majorValue < 0 || minorValue < 0)
        {
            return false;
        }

        return string.Equals(majorValue.ToString(), major, StringComparison.Ordinal) &&
               string.Equals(minorValue.ToString(), minor, StringComparison.Ordinal);
    }

    internal static async Task<IResult> InvokeSkill(
        HttpContext http,
        string guid,
        [FromServices] IUserSkillRunService runService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(runService);

        // The run is attributed to the caller's scope so it surfaces in their observatory.
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var loggerFactory = http.RequestServices.GetService<ILoggerFactory>();
        var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(
            http,
            http.RequestServices.GetService<IExternalIdentityBindingQueryPort>(),
            loggerFactory?.CreateLogger("Aevatar.Mainnet.Host.Api.WorkflowSkills"),
            ct);
        if (!callerCredential.Succeeded ||
            callerCredential.Credential == null ||
            string.IsNullOrWhiteSpace(callerCredential.Credential.BearerToken))
        {
            return Results.Unauthorized();
        }

        SkillInvokeRequest body;
        try
        {
            body = await http.Request.ReadFromJsonAsync<SkillInvokeRequest>(ct) ?? new SkillInvokeRequest();
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.BadRequest(new { error = "invalid_json" });
        }

        var outcome = await runService.InvokeOnceAsync(
            guid,
            callerCredential.Credential,
            scopeId,
            body.Prompt ?? string.Empty,
            ct);
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

        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var loggerFactory = http.RequestServices.GetService<ILoggerFactory>();
        var callerCredential = await WorkflowCallerCredentialExtractor.ExtractAsync(
            http,
            http.RequestServices.GetService<IExternalIdentityBindingQueryPort>(),
            loggerFactory?.CreateLogger("Aevatar.Mainnet.Host.Api.WorkflowSkills"),
            ct);
        if (!callerCredential.Succeeded ||
            callerCredential.Credential == null ||
            string.IsNullOrWhiteSpace(callerCredential.Credential.BearerToken))
        {
            return Results.Unauthorized();
        }

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
            callerCredential.Credential,
            scopeId,
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
