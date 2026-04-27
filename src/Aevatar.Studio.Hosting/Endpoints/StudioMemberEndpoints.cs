using Aevatar.Hosting;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
    }

    internal static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        string scopeId,
        CreateStudioMemberRequest request,
        IStudioMemberService memberService,
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
        IStudioMemberService memberService,
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
        IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var detail = await memberService.GetAsync(scopeId, memberId, ct);
            return detail == null ? Results.NotFound() : Results.Ok(detail);
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
        IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            return Results.Ok(await memberService.BindAsync(scopeId, memberId, request, ct));
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
        IStudioMemberService memberService,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        try
        {
            var binding = await memberService.GetBindingAsync(scopeId, memberId, ct);
            return binding == null ? Results.NotFound() : Results.Ok(binding);
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
