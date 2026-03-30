using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Authorize]
[Route("api/user-config")]
public sealed class UserConfigController : ControllerBase
{
    private readonly IUserConfigStore _userConfigStore;
    private readonly ILogger<UserConfigController> _logger;

    public UserConfigController(
        IUserConfigStore userConfigStore,
        ILogger<UserConfigController> logger)
    {
        _userConfigStore = userConfigStore;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<UserConfig>> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _userConfigStore.GetAsync(cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected error reading user config");
            return StatusCode(502, new { message = "User config storage is temporarily unavailable." });
        }
    }

    [HttpPut]
    public async Task<ActionResult<UserConfig>> Save(
        [FromBody] UserConfig request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _userConfigStore.SaveAsync(request, cancellationToken);
            return Ok(request);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected error saving user config");
            return StatusCode(502, new { message = "User config storage is temporarily unavailable." });
        }
    }
}
