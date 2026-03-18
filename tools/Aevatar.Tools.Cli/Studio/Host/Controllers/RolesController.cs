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
