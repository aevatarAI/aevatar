using Aevatar.Tools.Cli.Studio.Application.Contracts;
using Aevatar.Tools.Cli.Studio.Application.Exceptions;
using Aevatar.Tools.Cli.Studio.Application.Services;
using Aevatar.Tools.Cli.Studio.Domain.Models;
using Aevatar.Tools.Cli.Studio.Host.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Tools.Cli.Studio.Host.Controllers;

[ApiController]
[Route("api/bundles")]
public sealed class BundlesController : ControllerBase
{
    private readonly BundleService _bundleService;

    public BundlesController(BundleService bundleService)
    {
        _bundleService = bundleService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectIndexEntry>>> List(CancellationToken cancellationToken) =>
        Ok(await _bundleService.ListAsync(cancellationToken));

    [HttpGet("{bundleId}")]
    public async Task<ActionResult<WorkflowBundle>> Get(string bundleId, CancellationToken cancellationToken)
    {
        var bundle = await _bundleService.GetAsync(bundleId, cancellationToken);
        return bundle is null ? NotFound() : Ok(bundle);
    }

    [HttpPost]
    public async Task<ActionResult<WorkflowBundle>> Create(
        [FromBody] SaveWorkflowBundleRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var bundle = await _bundleService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { bundleId = bundle.Id }, bundle);
        }
        catch (StudioValidationException exception)
        {
            return BadRequest(ToValidationResponse(exception));
        }
    }

    [HttpPut("{bundleId}")]
    public async Task<ActionResult<WorkflowBundle>> Update(
        string bundleId,
        [FromBody] SaveWorkflowBundleRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var bundle = await _bundleService.UpdateAsync(bundleId, request, cancellationToken);
            return bundle is null ? NotFound() : Ok(bundle);
        }
        catch (StudioValidationException exception)
        {
            return BadRequest(ToValidationResponse(exception));
        }
    }

    [HttpDelete("{bundleId}")]
    public async Task<IActionResult> Delete(string bundleId, CancellationToken cancellationToken) =>
        await _bundleService.DeleteAsync(bundleId, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("{bundleId}/clone")]
    public async Task<ActionResult<WorkflowBundle>> Clone(
        string bundleId,
        [FromBody] CloneWorkflowBundleRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var bundle = await _bundleService.CloneAsync(bundleId, request ?? new CloneWorkflowBundleRequest(), cancellationToken);
            return bundle is null
                ? NotFound()
                : CreatedAtAction(nameof(Get), new { bundleId = bundle.Id }, bundle);
        }
        catch (StudioValidationException exception)
        {
            return BadRequest(ToValidationResponse(exception));
        }
    }

    [HttpGet("{bundleId}/versions")]
    public async Task<ActionResult<IReadOnlyList<WorkflowVersion>>> GetVersions(
        string bundleId,
        CancellationToken cancellationToken)
    {
        var versions = await _bundleService.GetVersionsAsync(bundleId, cancellationToken);
        return versions is null ? NotFound() : Ok(versions);
    }

    [HttpPost("import")]
    public async Task<ActionResult<WorkflowBundle>> Import(
        [FromBody] ImportWorkflowBundleRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var bundle = await _bundleService.ImportAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { bundleId = bundle.Id }, bundle);
        }
        catch (StudioValidationException exception)
        {
            return BadRequest(ToValidationResponse(exception));
        }
    }

    [HttpGet("{bundleId}/export")]
    public async Task<ActionResult<WorkflowBundleExportResult>> Export(
        string bundleId,
        CancellationToken cancellationToken)
    {
        var result = await _bundleService.ExportAsync(bundleId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    private static ValidationErrorResponse ToValidationResponse(StudioValidationException exception) =>
        new(exception.Message, exception.Findings);
}
