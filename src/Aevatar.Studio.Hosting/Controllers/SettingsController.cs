using Aevatar.Studio.Application;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsService _settingsService;

    public SettingsController(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [HttpGet]
    public async Task<ActionResult<StudioSettingsResponse>> Get(CancellationToken cancellationToken) =>
        Ok(await _settingsService.GetAsync(cancellationToken));

    [HttpPut]
    public async Task<ActionResult<StudioSettingsResponse>> Save(
        [FromBody] UpdateStudioSettingsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _settingsService.SaveAsync(request, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("runtime/test")]
    public async Task<ActionResult<RuntimeConnectionTestResponse>> TestRuntime(
        [FromBody] RuntimeConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _settingsService.TestRuntimeAsync(request, cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }
}
