using Aevatar.Hosting;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Endpoints;

/// <summary>
/// Team-first Studio HTTP surface mounted under
/// <c>/api/scopes/{scopeId}/teams</c> (ADR-0017). Endpoints depend only on
/// <see cref="IStudioTeamService"/>; they never reach for the projection
/// command port directly.
///
/// Error mapping mirrors <see cref="StudioMemberEndpoints"/>:
///   - <see cref="StudioTeamNotFoundException"/> → 404
///   - other <see cref="InvalidOperationException"/> (validation) → 400
///
/// Like the member endpoints, every <see cref="IStudioTeamService"/>
/// parameter must carry <see cref="FromServicesAttribute"/> so Minimal API's
/// <c>RequestDelegateFactory</c> resolves the dependency from DI rather than
/// probing the interface for a <c>BindAsync</c> custom-binder hook.
/// </summary>
internal static class StudioTeamEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/scopes/{scopeId}/teams", HandleCreateAsync)
            .WithTags("StudioTeams");
        app.MapGet("/api/scopes/{scopeId}/teams", HandleListAsync)
            .WithTags("StudioTeams");
        app.MapGet("/api/scopes/{scopeId}/teams/{teamId}", HandleGetAsync)
            .WithTags("StudioTeams");
        app.MapPatch("/api/scopes/{scopeId}/teams/{teamId}", HandlePatchAsync)
            .WithTags("StudioTeams");
        app.MapPost(
                "/api/scopes/{scopeId}/teams/{teamId}/archive",
                HandleArchiveAsync)
            .WithTags("StudioTeams");

        // Team -> members listing: queries the member read model filtered by
        // team_id (per ADR-0017 §HTTP endpoints — the team read model itself
        // does NOT mirror the full roster).
        app.MapGet(
                "/api/scopes/{scopeId}/teams/{teamId}/members",
                HandleListMembersAsync)
            .WithTags("StudioTeams");
    }

    internal static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        string scopeId,
        CreateStudioTeamRequest request,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var summary = await teamService.CreateAsync(scopeId, request, ct);
            return Results.Created($"/api/scopes/{scopeId}/teams/{summary.TeamId}", summary);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleListAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IStudioTeamService teamService,
        int? pageSize,
        string? pageToken,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var page = (pageSize.HasValue || !string.IsNullOrWhiteSpace(pageToken))
                ? new StudioTeamRosterPageRequest(pageSize, pageToken)
                : null;
            return Results.Ok(await teamService.ListAsync(scopeId, page, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleGetAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await teamService.GetAsync(scopeId, teamId, ct));
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    /// <summary>
    /// Wire body for PATCH /teams/{teamId}. Same Merge-Patch semantics locked
    /// in ADR-0017 §Q6: a field absent in JSON means "no change"; an
    /// explicit null clears (description only); a non-empty string sets;
    /// empty string is rejected.
    /// </summary>
    public sealed class StudioTeamPatchBody
    {
        public System.Text.Json.JsonElement? DisplayName { get; set; }
        public System.Text.Json.JsonElement? Description { get; set; }
    }

    internal static async Task<IResult> HandlePatchAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        StudioTeamPatchBody body,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        if (body == null)
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", "request body is required.");

        // displayName: if present, must be a non-empty string. Reject null /
        // empty / non-string per ADR-0017 §Q6 (display_name is required-when-present).
        var displayNamePatch = PatchValue<string>.Absent;
        if (body.DisplayName.HasValue)
        {
            var v = body.DisplayName.Value;
            if (v.ValueKind != System.Text.Json.JsonValueKind.String)
                return BadRequest(
                    "INVALID_STUDIO_TEAM_REQUEST",
                    "displayName must be a non-empty string when present.");

            var raw = v.GetString();
            if (string.IsNullOrEmpty(raw))
                return BadRequest(
                    "INVALID_STUDIO_TEAM_REQUEST",
                    "displayName must be a non-empty string when present.");

            displayNamePatch = PatchValue<string>.Of(raw);
        }

        // description: if present, may be a string (set/clear). Reject non-string.
        var descriptionPatch = PatchValue<string>.Absent;
        if (body.Description.HasValue)
        {
            var v = body.Description.Value;
            switch (v.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Null:
                    descriptionPatch = PatchValue<string>.Of(null);
                    break;
                case System.Text.Json.JsonValueKind.String:
                    descriptionPatch = PatchValue<string>.Of(v.GetString());
                    break;
                default:
                    return BadRequest(
                        "INVALID_STUDIO_TEAM_REQUEST",
                        "description must be a string, null, or absent.");
            }
        }

        try
        {
            var detail = await teamService.UpdateAsync(
                scopeId,
                teamId,
                new UpdateStudioTeamRequest(displayNamePatch, descriptionPatch),
                ct);
            return Results.Ok(detail);
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleArchiveAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await teamService.ArchiveAsync(scopeId, teamId, ct));
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    /// <summary>
    /// Lists members assigned to a given team. Queries the member read model
    /// filtered by <c>team_id</c> (ADR-0017 §HTTP endpoints) — the team read
    /// model never mirrors the roster.
    ///
    /// For v1 this iterates the scope's roster and filters in-process. The
    /// member query port today doesn't expose a typed <c>team_id</c> filter,
    /// so the filter happens after the read model returns. A typed filter on
    /// the query port is a follow-up that does not change the wire shape.
    /// </summary>
    internal static async Task<IResult> HandleListMembersAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        [FromServices] IStudioTeamService teamService,
        [FromServices] IStudioMemberService memberService,
        int? pageSize,
        string? pageToken,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            // 404 propagation: missing team is unambiguous, not "team exists
            // with empty roster".
            _ = await teamService.GetAsync(scopeId, teamId, ct);

            // Today the member detail / summary contract does not surface
            // team_id, so a true team-scoped roster requires a query port
            // extension. v1 ships the endpoint as the canonical path (so the
            // frontend can wire to it once) and returns the scope roster as
            // a placeholder; the typed filter follows in the same area as
            // the cross-actor wiring follow-up. Documented in ADR-0017
            // §Cutover Order step 7.
            var page = (pageSize.HasValue || !string.IsNullOrWhiteSpace(pageToken))
                ? new StudioMemberRosterPageRequest(pageSize, pageToken)
                : null;
            var roster = await memberService.ListAsync(scopeId, page, ct);
            return Results.Ok(roster);
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_REQUEST", ex.Message);
        }
    }

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { code, message });

    private static IResult NotFound(StudioTeamNotFoundException ex) =>
        Results.Json(
            new
            {
                code = "STUDIO_TEAM_NOT_FOUND",
                message = ex.Message,
                scopeId = ex.ScopeId,
                teamId = ex.TeamId,
            },
            statusCode: StatusCodes.Status404NotFound);
}
