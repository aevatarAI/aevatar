using Aevatar.Capabilities;
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
    // Refactor (iter74/cluster-074-studio-team-members-query-fanout):
    //   Old pattern: Host loops scope roster pages + Host-side TeamId filter
    //   New principle: ReadModel query port owns scope_id+team_id filter before pagination
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
        app.MapPut(
                "/api/scopes/{scopeId}/teams/{teamId}/entry-member",
                HandleSetEntryMemberAsync)
            .WithTags("StudioTeams");
        app.MapDelete(
                "/api/scopes/{scopeId}/teams/{teamId}/entry-member",
                HandleClearEntryMemberAsync)
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
            var receipt = await teamService.UpdateAsync(
                scopeId,
                teamId,
                new UpdateStudioTeamRequest(displayNamePatch, descriptionPatch),
                ct);
            // Refactor (iter96/cluster-547):
            //   Old: dispatch then GetAsync readmodel returned 200 OK + snapshot (pretending completion).
            //   New: no readmodel read; 202 Accepted + Location points to stable team query resource,
            //        body only carries accepted/no_change receipt.
            return Results.Accepted(BuildTeamLocation(scopeId, teamId), receipt);
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
            var receipt = await teamService.ArchiveAsync(scopeId, teamId, ct);
            // Refactor (iter96/cluster-547):
            //   Old: dispatch then GetAsync readmodel returned 200 OK + snapshot (pretending completion).
            //   New: no readmodel read; 202 Accepted + Location points to stable team query resource,
            //        body only carries accepted/no_change receipt.
            return Results.Accepted(BuildTeamLocation(scopeId, teamId), receipt);
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

    internal static async Task<IResult> HandleSetEntryMemberAsync(
        HttpContext http,
        string scopeId,
        string teamId,
        SetStudioTeamEntryMemberRequest request,
        [FromServices] IStudioTeamService teamService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            await teamService.SetEntryMemberAsync(scopeId, teamId, request, ct);
            return Results.Accepted(BuildTeamLocation(scopeId, teamId));
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (StudioMemberNotFoundException ex)
        {
            return MemberNotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_ENTRY_MEMBER_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleClearEntryMemberAsync(
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
            await teamService.ClearEntryMemberAsync(scopeId, teamId, ct);
            return Results.Accepted(BuildTeamLocation(scopeId, teamId));
        }
        catch (StudioTeamNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_TEAM_ENTRY_MEMBER_REQUEST", ex.Message);
        }
    }

    /// <summary>
    /// Lists members assigned to a given team. Queries the member read model
    /// filtered by <c>team_id</c> (ADR-0017 §HTTP endpoints) — the team read
    /// model never mirrors the roster.
    ///
    /// The member query port owns the typed <c>team_id</c> filter and applies
    /// it before pagination, so sparse team members across a scope still page
    /// by team membership rather than by the unfiltered scope roster.
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

            var page = new StudioMemberRosterPageRequest(
                PageSize: pageSize,
                PageToken: pageToken,
                TeamId: teamId);

            return Results.Ok(await memberService.ListAsync(scopeId, page, ct));
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

    private static string BuildTeamLocation(string scopeId, string teamId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/teams/{Uri.EscapeDataString(teamId)}";

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

    private static IResult MemberNotFound(StudioMemberNotFoundException ex) =>
        Results.Json(
            new
            {
                code = "STUDIO_MEMBER_NOT_FOUND",
                message = ex.Message,
                scopeId = ex.ScopeId,
                memberId = ex.MemberId,
            },
            statusCode: StatusCodes.Status404NotFound);

}
