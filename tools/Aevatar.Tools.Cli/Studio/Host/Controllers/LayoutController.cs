using Aevatar.Tools.Cli.Studio.Application.Services;
using Aevatar.Tools.Cli.Studio.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Tools.Cli.Studio.Host.Controllers;

[ApiController]
[Route("api/bundles/{bundleId}/layout")]
public sealed class LayoutController : ControllerBase
{
    private readonly BundleService _bundleService;

    public LayoutController(BundleService bundleService)
    {
        _bundleService = bundleService;
    }

    [HttpGet]
    public async Task<ActionResult<WorkflowLayoutDocument>> GetLayout(
        string bundleId,
        CancellationToken cancellationToken)
    {
        var layout = await _bundleService.GetLayoutAsync(bundleId, cancellationToken);
        return layout is null ? NotFound() : Ok(layout);
    }

    [HttpPut]
    public async Task<ActionResult<WorkflowLayoutDocument>> SaveLayout(
        string bundleId,
        [FromBody] WorkflowLayoutDocument layout,
        CancellationToken cancellationToken)
    {
        var updatedLayout = await _bundleService.SaveLayoutAsync(bundleId, layout, cancellationToken);
        return updatedLayout is null ? NotFound() : Ok(updatedLayout);
    }
}
