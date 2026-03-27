using Aevatar.Studio.Application;
using Aevatar.Studio.Infrastructure.ScopeResolution;
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
        CancellationToken cancellationToken) =>
        Ok(await _executionService.StartAsync(request, cancellationToken));

    [HttpPost("{executionId}/resume")]
    public async Task<ActionResult<ExecutionDetail>> Resume(
        string executionId,
        [FromBody] ResumeExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var execution = await _executionService.ResumeAsync(executionId, request, cancellationToken);
        return execution is null ? NotFound() : Ok(execution);
    }

    [HttpPost("{executionId}/stop")]
    public async Task<ActionResult<ExecutionDetail>> Stop(
        string executionId,
        [FromBody] StopExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var execution = await _executionService.StopAsync(executionId, request, cancellationToken);
        return execution is null ? NotFound() : Ok(execution);
    }
}
