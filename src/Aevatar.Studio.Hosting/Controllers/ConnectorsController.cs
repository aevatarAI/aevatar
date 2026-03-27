using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Route("api/connectors")]
public sealed class ConnectorsController : ControllerBase
{
    private readonly ConnectorService _connectorService;

    public ConnectorsController(ConnectorService connectorService)
    {
        _connectorService = connectorService;
    }

    [HttpGet]
    public async Task<ActionResult<ConnectorCatalogResponse>> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _connectorService.GetCatalogAsync(cancellationToken));
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
    public async Task<ActionResult<ConnectorDraftResponse>> GetDraft(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _connectorService.GetDraftAsync(cancellationToken));
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
    public async Task<ActionResult<ConnectorCatalogResponse>> Save(
        [FromBody] SaveConnectorCatalogRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _connectorService.SaveCatalogAsync(request, cancellationToken));
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
    public async Task<ActionResult<ImportConnectorCatalogResponse>> Import(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0)
        {
            return BadRequest(new { message = "Select a connector catalog JSON file to import." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await _connectorService.ImportCatalogAsync(file.FileName, stream, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPut("draft")]
    public async Task<ActionResult<ConnectorDraftResponse>> SaveDraft(
        [FromBody] SaveConnectorDraftRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _connectorService.SaveDraftAsync(request, cancellationToken));
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
            await _connectorService.DeleteDraftAsync(cancellationToken);
            return NoContent();
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
