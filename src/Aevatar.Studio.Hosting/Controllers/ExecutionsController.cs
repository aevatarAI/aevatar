using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Route("api/executions")]
public sealed class ExecutionsController : ControllerBase
{
    private readonly ExecutionService _executionService;

    public ExecutionsController(ExecutionService executionService)
    {
        _executionService = executionService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ExecutionSummary>>> List(CancellationToken cancellationToken) =>
        Ok(await _executionService.ListAsync(cancellationToken));

    [HttpGet("{executionId}")]
    public async Task<ActionResult<ExecutionDetail>> Get(string executionId, CancellationToken cancellationToken)
    {
        var execution = await _executionService.GetAsync(executionId, cancellationToken);
        return execution is null ? NotFound() : Ok(execution);
    }

    [HttpPost]
    public async Task<ActionResult<ExecutionDetail>> Start(
        [FromBody] StartExecutionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _executionService.StartAsync(request, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            // Scope mismatches, missing scope_id claim, and missing scope/workflow targets are
            // client-side contract violations, not server faults. Surface them as 400 to match
            // the Roles/Connectors controller convention; otherwise they bubble up as 500.
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("{executionId}/resume")]
    public async Task<ActionResult<ExecutionDetail>> Resume(
        string executionId,
        [FromBody] ResumeExecutionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var execution = await _executionService.ResumeAsync(executionId, request, cancellationToken);
            return execution is null ? NotFound() : Ok(execution);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("{executionId}/stop")]
    public async Task<ActionResult<ExecutionDetail>> Stop(
        string executionId,
        [FromBody] StopExecutionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var execution = await _executionService.StopAsync(executionId, request, cancellationToken);
            return execution is null ? NotFound() : Ok(execution);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }
}
