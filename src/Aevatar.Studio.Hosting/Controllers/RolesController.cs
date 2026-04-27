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
        var effectiveRequest = ApplyIfMatch(request);
        try
        {
            var response = await _roleCatalogService.SaveCatalogAsync(effectiveRequest, cancellationToken);
            ETagSupport.WriteETag(Response, response.Version);
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
        var effectiveRequest = ApplyIfMatch(request);
        try
        {
            var response = await _roleCatalogService.SaveDraftAsync(effectiveRequest, cancellationToken);
            ETagSupport.WriteETag(Response, response.Version);
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
        var expectedVersion = ETagSupport.TryParseIfMatch(Request);
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

    private SaveRoleCatalogRequest ApplyIfMatch(SaveRoleCatalogRequest request)
    {
        if (request.ExpectedVersion is not null)
            return request;
        var expected = ETagSupport.TryParseIfMatch(Request);
        return expected is null ? request : request with { ExpectedVersion = expected };
    }

    private SaveRoleDraftRequest ApplyIfMatch(SaveRoleDraftRequest request)
    {
        if (request.ExpectedVersion is not null)
            return request;
        var expected = ETagSupport.TryParseIfMatch(Request);
        return expected is null ? request : request with { ExpectedVersion = expected };
    }
}
