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
            return Ok(await _roleCatalogService.GetCatalogAsync(cancellationToken));
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
            return Ok(await _roleCatalogService.GetDraftAsync(cancellationToken));
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
        try
        {
            return Ok(await _roleCatalogService.SaveCatalogAsync(request, cancellationToken));
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
        try
        {
            return Ok(await _roleCatalogService.SaveDraftAsync(request, cancellationToken));
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
        try
        {
            await _roleCatalogService.DeleteDraftAsync(cancellationToken);
            return NoContent();
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
}
