using System.Text.Json.Serialization;
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
    private readonly IUserConfigService _userConfigService;
    private readonly IUserLlmPreferenceService _llmPreferenceService;
    private readonly ILogger<UserConfigController> _logger;

    public UserConfigController(
        IUserConfigService userConfigService,
        IUserLlmPreferenceService llmPreferenceService,
        ILogger<UserConfigController> logger)
    {
        _userConfigService = userConfigService;
        _llmPreferenceService = llmPreferenceService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<UserConfig>> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _userConfigService.GetAsync(cancellationToken));
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
    public async Task<ActionResult<UserConfigSaveReceiptResponse>> Save(
        [FromBody] SaveUserConfigRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await _userConfigService.SaveAsync(
                ExtractBearerToken(),
                new SaveUserConfigCommand(
                    request.RuntimeMode,
                    request.LocalRuntimeBaseUrl,
                    request.RemoteRuntimeBaseUrl,
                    request.GithubUsername,
                    request.MaxToolRounds),
                cancellationToken);
            return Accepted(UserConfigSaveReceiptResponse.FromApplication(receipt));
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

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record SaveUserConfigRequest(
        [property: JsonPropertyName("runtimeMode")] string? RuntimeMode = null,
        [property: JsonPropertyName("localRuntimeBaseUrl")] string? LocalRuntimeBaseUrl = null,
        [property: JsonPropertyName("remoteRuntimeBaseUrl")] string? RemoteRuntimeBaseUrl = null,
        [property: JsonPropertyName("githubUsername")] string? GithubUsername = null,
        [property: JsonPropertyName("maxToolRounds")] int? MaxToolRounds = null);

    [HttpGet("llm")]
    public async Task<ActionResult<UserLlmSettingsResponse>> GetLlmSettings(CancellationToken cancellationToken)
    {
        try
        {
            var view = await _llmPreferenceService
                .GetSettingsAsync(ExtractBearerToken(), cancellationToken)
                .ConfigureAwait(false);
            return Ok(UserLlmSettingsResponse.FromApplication(view));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to fetch user LLM settings");
            return StatusCode(502, new { message = "LLM settings are temporarily unavailable." });
        }
    }

    [HttpPut("llm")]
    public async Task<ActionResult<UserConfigSaveReceiptResponse>> SaveLlmSettings(
        [FromBody] SaveUserLlmSettingsRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request is null)
                return BadRequest(new { message = "Request body is required." });

            var receipt = await _userConfigService.SaveLlmPreferenceAsync(
                ExtractBearerToken(),
                request.ToIntent(),
                cancellationToken).ConfigureAwait(false);
            return Accepted(UserConfigSaveReceiptResponse.FromApplication(receipt));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save user LLM settings");
            return StatusCode(502, new { message = "User LLM settings are temporarily unavailable." });
        }
    }

    [HttpGet("runtime")]
    public async Task<ActionResult<UserConfigRuntimeView>> GetRuntime(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _userConfigService.GetRuntimeAsync(cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected error reading user runtime config");
            return StatusCode(502, new { message = "User runtime config is temporarily unavailable." });
        }
    }

    private string? ExtractBearerToken()
    {
        var header = HttpContext.Request.Headers.Authorization.ToString().Trim();
        if (string.IsNullOrWhiteSpace(header))
            return null;

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }
}
