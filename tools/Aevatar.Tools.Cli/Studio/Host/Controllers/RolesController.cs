using Aevatar.Tools.Cli.Studio.Application.Contracts;
using Aevatar.Tools.Cli.Studio.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Tools.Cli.Studio.Host.Controllers;

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
    public async Task<ActionResult<RoleCatalogResponse>> Get(CancellationToken cancellationToken) =>
        Ok(await _roleCatalogService.GetCatalogAsync(cancellationToken));

    [HttpGet("draft")]
    public async Task<ActionResult<RoleDraftResponse>> GetDraft(CancellationToken cancellationToken) =>
        Ok(await _roleCatalogService.GetDraftAsync(cancellationToken));

    [HttpPut]
    public async Task<ActionResult<RoleCatalogResponse>> Save(
        [FromBody] SaveRoleCatalogRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _roleCatalogService.SaveCatalogAsync(request, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("import-local")]
    public async Task<ActionResult<ImportRoleCatalogResponse>> ImportLocal(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _roleCatalogService.ImportLocalCatalogAsync(cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPut("draft")]
    public async Task<ActionResult<RoleDraftResponse>> SaveDraft(
        [FromBody] SaveRoleDraftRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _roleCatalogService.SaveDraftAsync(request, cancellationToken));

    [HttpDelete("draft")]
    public async Task<IActionResult> DeleteDraft(CancellationToken cancellationToken)
    {
        await _roleCatalogService.DeleteDraftAsync(cancellationToken);
        return NoContent();
    }
}
