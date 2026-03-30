using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Hosting.Controllers;

[ApiController]
[Route("api/user-config")]
public sealed class UserConfigController : ControllerBase
{
    private readonly IUserConfigStore _userConfigStore;

    public UserConfigController(IUserConfigStore userConfigStore)
    {
        _userConfigStore = userConfigStore;
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
    }
}
