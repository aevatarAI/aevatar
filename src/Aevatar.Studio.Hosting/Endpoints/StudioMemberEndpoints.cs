using Aevatar.Hosting;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Endpoints;

/// <summary>
/// Member-first Studio HTTP surface mounted under
/// <c>/api/scopes/{scopeId}/members</c>. Endpoints depend only on
/// <see cref="IStudioMemberService"/>; they never reach for the platform
/// scope binding port directly. ServiceId is never accepted as a user-facing
/// input — Studio binds to the member's own stable
/// <c>publishedServiceId</c>.
///
/// Error mapping:
///   - <see cref="StudioMemberNotFoundException"/> → 404
///   - other <see cref="InvalidOperationException"/> (validation) → 400
///
/// IMPORTANT: every <see cref="IStudioMemberService"/> parameter must carry
/// the <see cref="FromServicesAttribute"/>. Minimal API's
/// <c>RequestDelegateFactory</c> probes parameter types for a
/// <c>BindAsync</c> custom-binder hook; the interface itself defines an
/// instance method named <c>BindAsync</c>, which the binder then rejects
/// with <c>"BindAsync method found on IStudioMemberService with incorrect
/// format"</c> at host startup — before any request is served. The
/// attribute short-circuits that probe and resolves the dependency from DI
/// instead. Removing it will pass unit tests (which call the handlers
/// directly) but break the entire mainnet host composition.
/// </summary>
internal static class StudioMemberEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/scopes/{scopeId}/members", HandleCreateAsync)
            .WithTags("StudioMembers");
        app.MapGet("/api/scopes/{scopeId}/members", HandleListAsync)
            .WithTags("StudioMembers");
        app.MapGet("/api/scopes/{scopeId}/members/{memberId}", HandleGetAsync)
            .WithTags("StudioMembers");
        app.MapPut("/api/scopes/{scopeId}/members/{memberId}/binding", HandleBindAsync)
            .WithTags("StudioMembers");
        app.MapGet("/api/scopes/{scopeId}/members/{memberId}/binding", HandleGetBindingAsync)
            .WithTags("StudioMembers");
        app.MapGet(
                "/api/scopes/{scopeId}/members/{memberId}/endpoints/{endpointId}/contract",
                HandleGetEndpointContractAsync)
            .WithTags("StudioMembers");
        app.MapPost(
                "/api/scopes/{scopeId}/members/{memberId}/binding/revisions/{revisionId}:activate",
                HandleActivateBindingRevisionAsync)
            .WithTags("StudioMembers");
        app.MapPost(
                "/api/scopes/{scopeId}/members/{memberId}/binding/revisions/{revisionId}:retire",
                HandleRetireBindingRevisionAsync)
            .WithTags("StudioMembers");

        // ADR-0017: PATCH a member's team assignment. Body shape carries
        // Merge-Patch semantics for `teamId` — see HandlePatchAsync.
        app.MapPatch("/api/scopes/{scopeId}/members/{memberId}", HandlePatchAsync)
            .WithTags("StudioMembers");
    }

    internal static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        string scopeId,
        CreateStudioMemberRequest request,
        [FromServices] IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var summary = await memberService.CreateAsync(scopeId, request, ct);
            return Results.Created($"/api/scopes/{scopeId}/members/{summary.MemberId}", summary);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_MEMBER_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleListAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IStudioMemberService memberService,
        int? pageSize,
        string? pageToken,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var page = (pageSize.HasValue || !string.IsNullOrWhiteSpace(pageToken))
                ? new StudioMemberRosterPageRequest(pageSize, pageToken)
                : null;
            return Results.Ok(await memberService.ListAsync(scopeId, page, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_MEMBER_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleGetAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        [FromServices] IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await memberService.GetAsync(scopeId, memberId, ct));
        }
        catch (StudioMemberNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_MEMBER_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleBindAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        UpdateStudioMemberBindingRequest request,
        [FromServices] IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Accepted(
                $"/api/scopes/{Uri.EscapeDataString(scopeId)}/members/{Uri.EscapeDataString(memberId)}/binding",
                await memberService.BindAsync(scopeId, memberId, request, ct));
        }
        catch (StudioMemberNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_MEMBER_BINDING", ex.Message);
        }
    }

    internal static async Task<IResult> HandleGetBindingAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        [FromServices] IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            // Three semantically distinct outcomes, three distinct HTTP shapes:
            //   - member missing                    → 404 STUDIO_MEMBER_NOT_FOUND
            //   - member exists, has been bound     → 200 { lastBinding: <contract> }
            //   - member exists, never bound        → 200 { lastBinding: null }
            // Bare `404 NotFound` for the "exists but never bound" case used
            // to overload 404 with two different meanings; the wrapper keeps
            // the response always a JSON object with a single nullable field.
            return Results.Ok(await memberService.GetBindingAsync(scopeId, memberId, ct));
        }
        catch (StudioMemberNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_MEMBER_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleGetEndpointContractAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string endpointId,
        [FromServices] IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var contract = await memberService.GetEndpointContractAsync(scopeId, memberId, endpointId, ct);
            if (contract == null)
            {
                return Results.NotFound(new
                {
                    code = "STUDIO_MEMBER_ENDPOINT_CONTRACT_NOT_FOUND",
                    message = $"Endpoint '{endpointId}' was not found on member '{memberId}' in scope '{scopeId}'.",
                });
            }

            return Results.Ok(contract);
        }
        catch (StudioMemberNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_MEMBER_ENDPOINT_CONTRACT_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleActivateBindingRevisionAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string revisionId,
        [FromServices] IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await memberService.ActivateBindingRevisionAsync(scopeId, memberId, revisionId, ct));
        }
        catch (StudioMemberNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_MEMBER_BINDING_ACTIVATION_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleRetireBindingRevisionAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        string revisionId,
        [FromServices] IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await memberService.RetireBindingRevisionAsync(scopeId, memberId, revisionId, ct));
        }
        catch (StudioMemberNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_MEMBER_BINDING_REVISION_REQUEST", ex.Message);
        }
    }

    /// <summary>
    /// Wire body for PATCH. Mirrors the JSON Merge-Patch table locked in
    /// ADR-0017 §Q6: <c>teamId</c> absent in JSON means "no change", explicit
    /// <c>null</c> means "unassign", a non-empty string means "assign /
    /// reassign", and the empty string is rejected with 400.
    ///
    /// Distinguishing absent from explicit null requires the field to be
    /// modeled as <see cref="JsonElement"/> rather than <see cref="string"/>?.
    /// The handler converts the wire form into a <see cref="PatchValue{T}"/>
    /// before the application layer sees it.
    /// </summary>
    public sealed class StudioMemberPatchBody
    {
        public System.Text.Json.JsonElement? TeamId { get; set; }
    }

    internal static async Task<IResult> HandlePatchAsync(
        HttpContext http,
        string scopeId,
        string memberId,
        StudioMemberPatchBody body,
        [FromServices] IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        if (body == null)
            return BadRequest("INVALID_STUDIO_MEMBER_REQUEST", "request body is required.");

        // Translate the wire body into the application contract. JsonElement
        // semantics:
        //   - body.TeamId == null            → field absent in JSON → no change
        //   - body.TeamId.ValueKind == Null  → explicit null → unassign
        //   - body.TeamId.ValueKind == String → assign / reassign (empty rejected)
        PatchValue<string> teamIdPatch;
        if (!body.TeamId.HasValue)
        {
            teamIdPatch = PatchValue<string>.Absent;
        }
        else
        {
            var jsonValue = body.TeamId.Value;
            switch (jsonValue.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Null:
                    teamIdPatch = PatchValue<string>.Of(null);
                    break;
                case System.Text.Json.JsonValueKind.String:
                    {
                        var raw = jsonValue.GetString();
                        if (string.IsNullOrEmpty(raw))
                            return BadRequest(
                                "INVALID_STUDIO_MEMBER_REQUEST",
                                "teamId must not be empty when present (use null to mean 'unassign').");
                        teamIdPatch = PatchValue<string>.Of(raw);
                        break;
                    }
                default:
                    return BadRequest(
                        "INVALID_STUDIO_MEMBER_REQUEST",
                        "teamId must be a string, null, or absent.");
            }
        }

        try
        {
            var detail = await memberService.UpdateAsync(
                scopeId, memberId, new UpdateStudioMemberRequest(teamIdPatch), ct);
            return Results.Ok(detail);
        }
        catch (StudioMemberNotFoundException ex)
        {
            return NotFound(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_STUDIO_MEMBER_REQUEST", ex.Message);
        }
    }

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { code, message });

    private static IResult NotFound(StudioMemberNotFoundException ex) =>
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
