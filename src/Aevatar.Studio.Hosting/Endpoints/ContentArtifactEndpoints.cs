using System.Security.Claims;
using Aevatar.Capabilities;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Studio.Hosting.Endpoints;

internal static class ContentArtifactEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/scopes/{scopeId}/content-artifacts", HandleCreateAsync).WithTags("ContentArtifacts");
        app.MapGet("/api/scopes/{scopeId}/content-artifacts", HandleListAsync).WithTags("ContentArtifacts");
        app.MapGet("/api/scopes/{scopeId}/content-artifacts/{artifactId}", HandleGetAsync).WithTags("ContentArtifacts");
        app.MapGet("/api/scopes/{scopeId}/content-artifacts/{artifactId}/revisions/current", HandleGetCurrentRevisionAsync).WithTags("ContentArtifacts");
        app.MapGet("/api/scopes/{scopeId}/content-artifacts/{artifactId}/revisions/{revisionId}", HandleGetRevisionAsync).WithTags("ContentArtifacts");
        app.MapGet("/api/scopes/{scopeId}/content-artifacts/{artifactId}/revisions/{revisionId}/content", HandleGetRevisionContentAsync).WithTags("ContentArtifacts");
        app.MapPost("/api/scopes/{scopeId}/content-artifacts/{artifactId}/revisions", HandleAppendRevisionAsync).WithTags("ContentArtifacts");
        app.MapPost("/api/scopes/{scopeId}/content-artifacts/{artifactId}:advance-current", HandleAdvanceCurrentRevisionAsync).WithTags("ContentArtifacts");
        app.MapPost("/api/scopes/{scopeId}/content-artifacts/{artifactId}/revisions/{revisionId}:redact", HandleRedactRevisionAsync).WithTags("ContentArtifacts");
        app.MapPost("/api/scopes/{scopeId}/content-artifacts/{artifactId}/revisions/{revisionId}:expire", HandleExpireRevisionAsync).WithTags("ContentArtifacts");
        app.MapPost("/api/scopes/{scopeId}/content-artifacts/{artifactId}:tombstone", HandleTombstoneAsync).WithTags("ContentArtifacts");
        app.MapPost("/api/scopes/{scopeId}/content-artifacts:attach-to-run", HandleAttachToRunAsync).WithTags("ContentArtifacts");
    }

    internal static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        string scopeId,
        CreateContentArtifactRequest request,
        [FromServices] IContentArtifactService service,
        CancellationToken ct)
    {
        if (!TryAuthorize(http, scopeId, out var principal, out var denied))
            return denied;
        try
        {
            var receipt = await service.CreateAsync(scopeId, request, principal, ct);
            return Results.Accepted(BuildLocation(scopeId, receipt.ArtifactId), receipt);
        }
        catch (ContentArtifactIdentityConflictException ex)
        {
            return Results.Conflict(new
            {
                code = "CONTENT_ARTIFACT_DEDUP_KEY_OCCUPIED",
                message = ex.Message,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_CONTENT_ARTIFACT_REQUEST", ex.Message);
        }
    }

    internal static async Task<IResult> HandleListAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IContentArtifactService service,
        int? pageSize,
        string? pageToken,
        string? teamId,
        string? kind,
        string? lifecycleStatus,
        string? workOrderId,
        string? runId,
        CancellationToken ct)
    {
        if (!TryAuthorize(http, scopeId, out var principal, out var denied))
            return denied;
        try
        {
            return Results.Ok(await service.ListAsync(
                scopeId,
                new ContentArtifactQueryRequest(
                    pageSize,
                    pageToken,
                    teamId,
                    kind,
                    lifecycleStatus,
                    workOrderId,
                    runId),
                principal,
                ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_CONTENT_ARTIFACT_QUERY", ex.Message);
        }
    }

    internal static Task<IResult> HandleGetAsync(
        HttpContext http,
        string scopeId,
        string artifactId,
        [FromServices] IContentArtifactService service,
        CancellationToken ct) =>
        HandleReadAsync(
            http,
            scopeId,
            principal => service.GetAsync(scopeId, artifactId, principal, ct));

    internal static Task<IResult> HandleGetRevisionAsync(
        HttpContext http,
        string scopeId,
        string artifactId,
        string revisionId,
        [FromServices] IContentArtifactService service,
        CancellationToken ct) =>
        HandleReadAsync(
            http,
            scopeId,
            principal => service.GetRevisionAsync(scopeId, artifactId, revisionId, principal, ct));

    internal static Task<IResult> HandleGetCurrentRevisionAsync(
        HttpContext http,
        string scopeId,
        string artifactId,
        [FromServices] IContentArtifactService service,
        CancellationToken ct) =>
        HandleReadAsync(
            http,
            scopeId,
            principal => service.GetCurrentRevisionAsync(scopeId, artifactId, principal, ct));

    internal static async Task<IResult> HandleGetRevisionContentAsync(
        HttpContext http,
        string scopeId,
        string artifactId,
        string revisionId,
        [FromServices] IContentArtifactService service,
        CancellationToken ct)
    {
        if (!TryAuthorize(http, scopeId, out var principal, out var denied))
            return denied;
        try
        {
            var content = await service.GetRevisionContentAsync(
                scopeId,
                artifactId,
                revisionId,
                principal,
                ct);
            return Results.File(content.Content, content.Reference.MediaType);
        }
        catch (ContentArtifactNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ContentArtifactContentUnavailableException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status410Gone);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_CONTENT_ARTIFACT_REQUEST", ex.Message);
        }
    }

    internal static Task<IResult> HandleAppendRevisionAsync(
        HttpContext http,
        string scopeId,
        string artifactId,
        AppendContentArtifactRevisionRequest request,
        [FromServices] IContentArtifactService service,
        CancellationToken ct) =>
        HandleCommandAsync(
            http,
            scopeId,
            artifactId,
            principal => service.AppendRevisionAsync(scopeId, artifactId, request, principal, ct));

    internal static Task<IResult> HandleAdvanceCurrentRevisionAsync(
        HttpContext http,
        string scopeId,
        string artifactId,
        AdvanceContentArtifactCurrentRevisionRequest request,
        [FromServices] IContentArtifactService service,
        CancellationToken ct) =>
        HandleCommandAsync(
            http,
            scopeId,
            artifactId,
            principal => service.AdvanceCurrentRevisionAsync(scopeId, artifactId, request, principal, ct));

    internal static Task<IResult> HandleRedactRevisionAsync(
        HttpContext http,
        string scopeId,
        string artifactId,
        string revisionId,
        RedactContentArtifactRevisionRequest request,
        [FromServices] IContentArtifactService service,
        CancellationToken ct) =>
        HandleCommandAsync(
            http,
            scopeId,
            artifactId,
            principal => service.RedactRevisionAsync(scopeId, artifactId, revisionId, request, principal, ct));

    internal static Task<IResult> HandleExpireRevisionAsync(
        HttpContext http,
        string scopeId,
        string artifactId,
        string revisionId,
        ExpireContentArtifactRevisionRequest request,
        [FromServices] IContentArtifactService service,
        CancellationToken ct) =>
        HandleCommandAsync(
            http,
            scopeId,
            artifactId,
            principal => service.ExpireRevisionAsync(scopeId, artifactId, revisionId, request, principal, ct));

    internal static Task<IResult> HandleTombstoneAsync(
        HttpContext http,
        string scopeId,
        string artifactId,
        TombstoneContentArtifactRequest request,
        [FromServices] IContentArtifactService service,
        CancellationToken ct) =>
        HandleCommandAsync(
            http,
            scopeId,
            artifactId,
            principal => service.TombstoneAsync(scopeId, artifactId, request, principal, ct));

    internal static async Task<IResult> HandleAttachToRunAsync(
        HttpContext http,
        string scopeId,
        AttachContentArtifactsToRunRequest request,
        [FromServices] IContentArtifactService service,
        CancellationToken ct)
    {
        if (!TryAuthorize(http, scopeId, out var principal, out var denied))
            return denied;
        try
        {
            return Results.Accepted(
                value: await service.AttachToRunAsync(scopeId, request, principal, ct));
        }
        catch (ContentArtifactNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ContentArtifactContentUnavailableException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status410Gone);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_CONTENT_ARTIFACT_COMMAND", ex.Message);
        }
    }

    private static async Task<IResult> HandleReadAsync<T>(
        HttpContext http,
        string scopeId,
        Func<ContentArtifactPrincipalContract, Task<T>> read)
    {
        if (!TryAuthorize(http, scopeId, out var principal, out var denied))
            return denied;
        try
        {
            return Results.Ok(await read(principal));
        }
        catch (ContentArtifactNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ContentArtifactContentUnavailableException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status410Gone);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_CONTENT_ARTIFACT_REQUEST", ex.Message);
        }
    }

    private static async Task<IResult> HandleCommandAsync(
        HttpContext http,
        string scopeId,
        string artifactId,
        Func<ContentArtifactPrincipalContract, Task<ContentArtifactAcceptedReceipt>> command)
    {
        if (!TryAuthorize(http, scopeId, out var principal, out var denied))
            return denied;
        try
        {
            var receipt = await command(principal);
            return Results.Accepted(BuildLocation(scopeId, receipt.ArtifactId), receipt);
        }
        catch (ContentArtifactNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ContentArtifactContentUnavailableException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status410Gone);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("INVALID_CONTENT_ARTIFACT_COMMAND", ex.Message);
        }
    }

    private static bool TryAuthorize(
        HttpContext http,
        string scopeId,
        out ContentArtifactPrincipalContract principal,
        out IResult denied)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out denied))
        {
            principal = null!;
            return false;
        }
        if (!TryResolvePrincipal(http.User, out principal))
        {
            denied = Results.Unauthorized();
            return false;
        }
        denied = null!;
        return true;
    }

    private static bool TryResolvePrincipal(
        ClaimsPrincipal user,
        out ContentArtifactPrincipalContract principal)
    {
        var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value?.Trim()
                      ?? user.FindFirst("sub")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(subject))
        {
            principal = null!;
            return false;
        }
        principal = new ContentArtifactPrincipalContract(subject, "user");
        return true;
    }

    private static string BuildLocation(string scopeId, string artifactId) =>
        $"/api/scopes/{Uri.EscapeDataString(scopeId)}/content-artifacts/{Uri.EscapeDataString(artifactId)}";

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { code, message });

    private static IResult NotFound(string message) =>
        Results.NotFound(new { code = "CONTENT_ARTIFACT_NOT_FOUND", message });
}
