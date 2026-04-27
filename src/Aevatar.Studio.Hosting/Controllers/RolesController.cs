using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Route("api/roles")]
public sealed class RolesController : ControllerBase
{
    private readonly RoleCatalogService _roleCatalogService;

    public RolesController(RoleCatalogService roleCatalogService)
    {
        _roleCatalogService = roleCatalogService;
    }

    [HttpGet]
    public async Task<ActionResult<RoleCatalogResponse>> Get(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _roleCatalogService.GetCatalogAsync(cancellationToken);
            ETagSupport.WriteETag(Response, response.Version);
            return Ok(response);
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.Message });
        }
    }

    [HttpGet("draft")]
    public async Task<ActionResult<RoleDraftResponse>> GetDraft(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _roleCatalogService.GetDraftAsync(cancellationToken);
            ETagSupport.WriteETag(Response, response.Version);
            return Ok(response);
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.Message });
        }
    }

    [HttpPut]
    public async Task<ActionResult<RoleCatalogResponse>> Save(
        [FromBody] SaveRoleCatalogRequest request,
        CancellationToken cancellationToken)
    {
        if (TryApplyIfMatch(request, out var effectiveRequest, out var malformed))
            return malformed!;
        try
        {
            var response = await _roleCatalogService.SaveCatalogAsync(effectiveRequest, cancellationToken);
            EmitETagIfDeterministic(response.Version);
            return Ok(response);
        }
        catch (EventStoreOptimisticConcurrencyException exception)
        {
            return Conflict(new { code = "VERSION_CONFLICT", message = exception.Message });
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.Message });
        }
    }

    [HttpPost("import")]
    public async Task<ActionResult<ImportRoleCatalogResponse>> Import(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0)
        {
            return BadRequest(new { message = "Select a role catalog JSON file to import." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await _roleCatalogService.ImportCatalogAsync(file.FileName, stream, cancellationToken));
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPut("draft")]
    public async Task<ActionResult<RoleDraftResponse>> SaveDraft(
        [FromBody] SaveRoleDraftRequest request,
        CancellationToken cancellationToken)
    {
        if (TryApplyIfMatch(request, out var effectiveRequest, out var malformed))
            return malformed!;
        try
        {
            var response = await _roleCatalogService.SaveDraftAsync(effectiveRequest, cancellationToken);
            EmitETagIfDeterministic(response.Version);
            return Ok(response);
        }
        catch (EventStoreOptimisticConcurrencyException exception)
        {
            return Conflict(new { code = "VERSION_CONFLICT", message = exception.Message });
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.Message });
        }
    }

    [HttpDelete("draft")]
    public async Task<IActionResult> DeleteDraft(CancellationToken cancellationToken)
    {
        var status = ETagSupport.ParseIfMatch(Request, out var ifMatchVersion);
        if (status == IfMatchStatus.Invalid)
            return MalformedIfMatch();

        long? expectedVersion = status == IfMatchStatus.Valid ? ifMatchVersion : null;
        try
        {
            await _roleCatalogService.DeleteDraftAsync(expectedVersion, cancellationToken);
            return NoContent();
        }
        catch (EventStoreOptimisticConcurrencyException exception)
        {
            return Conflict(new { code = "VERSION_CONFLICT", message = exception.Message });
        }
        catch (ChronoStorageServiceException exception)
        {
            return ChronoStorageErrorResponses.ToActionResult(exception);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.Message });
        }
    }

    /// <summary>
    /// When the request's If-Match header is malformed, returns true and yields a 400.
    /// When the header is valid AND the body specifies a different expectedVersion, returns
    /// true and yields a 400 — the request is internally inconsistent and would otherwise let
    /// the body silently bypass an explicit HTTP precondition. Otherwise binds the header's
    /// version (header is authoritative when present).
    /// </summary>
    private bool TryApplyIfMatch(SaveRoleCatalogRequest request, out SaveRoleCatalogRequest effective, out ActionResult? malformed)
    {
        var status = ETagSupport.ParseIfMatch(Request, out var headerVersion);
        if (status == IfMatchStatus.Invalid)
        {
            effective = request;
            malformed = MalformedIfMatch();
            return true;
        }

        if (status == IfMatchStatus.Valid)
        {
            if (request.ExpectedVersion is { } bodyVersion && bodyVersion != headerVersion)
            {
                effective = request;
                malformed = IfMatchBodyMismatch(headerVersion, bodyVersion);
                return true;
            }

            effective = request with { ExpectedVersion = headerVersion };
            malformed = null;
            return false;
        }

        effective = request;
        malformed = null;
        return false;
    }

    private bool TryApplyIfMatch(SaveRoleDraftRequest request, out SaveRoleDraftRequest effective, out ActionResult? malformed)
    {
        var status = ETagSupport.ParseIfMatch(Request, out var headerVersion);
        if (status == IfMatchStatus.Invalid)
        {
            effective = request;
            malformed = MalformedIfMatch();
            return true;
        }

        if (status == IfMatchStatus.Valid)
        {
            if (request.ExpectedVersion is { } bodyVersion && bodyVersion != headerVersion)
            {
                effective = request;
                malformed = IfMatchBodyMismatch(headerVersion, bodyVersion);
                return true;
            }

            effective = request with { ExpectedVersion = headerVersion };
            malformed = null;
            return false;
        }

        effective = request;
        malformed = null;
        return false;
    }

    private void EmitETagIfDeterministic(long version)
    {
        // Storage returns 0 when the post-write version is non-deterministic
        // (caller did not supply expected_version; projection lag would race a re-read).
        // Only emit ETag when we can guarantee the value reflects this write.
        if (version > 0)
            ETagSupport.WriteETag(Response, version);
    }

    private BadRequestObjectResult MalformedIfMatch() =>
        BadRequest(new
        {
            code = "MALFORMED_IF_MATCH",
            message = "If-Match must be a strong validator with a single non-negative integer version (e.g. \"5\").",
        });

    private BadRequestObjectResult IfMatchBodyMismatch(long headerVersion, long bodyVersion) =>
        BadRequest(new
        {
            code = "IF_MATCH_BODY_MISMATCH",
            message = $"If-Match header (\"{headerVersion}\") disagrees with body expectedVersion ({bodyVersion}). Send only one, or set them to the same value.",
        });
}
