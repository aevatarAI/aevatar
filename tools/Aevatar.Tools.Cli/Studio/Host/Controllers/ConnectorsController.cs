using Aevatar.Tools.Cli.Studio.Application.Contracts;
using Aevatar.Tools.Cli.Studio.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Tools.Cli.Studio.Host.Controllers;

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
    public async Task<ActionResult<ConnectorCatalogResponse>> Get(CancellationToken cancellationToken) =>
        Ok(await _connectorService.GetCatalogAsync(cancellationToken));

    [HttpGet("draft")]
    public async Task<ActionResult<ConnectorDraftResponse>> GetDraft(CancellationToken cancellationToken) =>
        Ok(await _connectorService.GetDraftAsync(cancellationToken));

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
    }

    [HttpPost("import-local")]
    public async Task<ActionResult<ImportConnectorCatalogResponse>> ImportLocal(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _connectorService.ImportLocalCatalogAsync(cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPut("draft")]
    public async Task<ActionResult<ConnectorDraftResponse>> SaveDraft(
        [FromBody] SaveConnectorDraftRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _connectorService.SaveDraftAsync(request, cancellationToken));

    [HttpDelete("draft")]
    public async Task<IActionResult> DeleteDraft(CancellationToken cancellationToken)
    {
        await _connectorService.DeleteDraftAsync(cancellationToken);
        return NoContent();
    }
}
