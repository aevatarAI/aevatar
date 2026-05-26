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
    private readonly IUserConfigQueryPort _queryPort;
    private readonly IUserConfigCommandService _commandService;
    private readonly IUserLlmPreferenceService _llmPreferenceService;
    private readonly ILogger<UserConfigController> _logger;

    public UserConfigController(
        IUserConfigQueryPort queryPort,
        IUserConfigCommandService commandService,
        IUserLlmPreferenceService llmPreferenceService,
        ILogger<UserConfigController> logger)
    {
        _queryPort = queryPort;
        _commandService = commandService;
        _llmPreferenceService = llmPreferenceService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<UserConfig>> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _queryPort.GetAsync(cancellationToken));
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
        [FromBody] SaveUserConfigRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _queryPort.GetAsync(cancellationToken);
            var merged = new UserConfig(
                DefaultModel: request.DefaultModel is null ? current.DefaultModel : request.DefaultModel.Trim(),
                PreferredLlmRoute: request.PreferredLlmRoute is null ? current.PreferredLlmRoute : UserConfigLlmRoute.Normalize(request.PreferredLlmRoute),
                RuntimeMode: request.RuntimeMode is null ? current.RuntimeMode : request.RuntimeMode.Trim(),
                LocalRuntimeBaseUrl: request.LocalRuntimeBaseUrl is null ? current.LocalRuntimeBaseUrl : request.LocalRuntimeBaseUrl.Trim(),
                RemoteRuntimeBaseUrl: request.RemoteRuntimeBaseUrl is null ? current.RemoteRuntimeBaseUrl : request.RemoteRuntimeBaseUrl.Trim(),
                GithubUsername: request.GithubUsername is null ? current.GithubUsername : NormalizeOptional(request.GithubUsername),
                MaxToolRounds: request.MaxToolRounds ?? current.MaxToolRounds);
            await _commandService.SaveAsync(merged, cancellationToken);
            return Ok(merged);
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

    public sealed record SaveUserConfigRequest(
        [property: JsonPropertyName("defaultModel")] string? DefaultModel = null,
        [property: JsonPropertyName("preferredLlmRoute")] string? PreferredLlmRoute = null,
        [property: JsonPropertyName("runtimeMode")] string? RuntimeMode = null,
        [property: JsonPropertyName("localRuntimeBaseUrl")] string? LocalRuntimeBaseUrl = null,
        [property: JsonPropertyName("remoteRuntimeBaseUrl")] string? RemoteRuntimeBaseUrl = null,
        [property: JsonPropertyName("githubUsername")] string? GithubUsername = null,
        [property: JsonPropertyName("maxToolRounds")] int? MaxToolRounds = null);

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    [HttpGet("llm")]
    public async Task<ActionResult<UserLlmSettingsView>> GetLlmSettings(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _llmPreferenceService
                .GetSettingsAsync(ExtractBearerToken(), cancellationToken)
                .ConfigureAwait(false));
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
    public async Task<ActionResult<UserLlmSettingsView>> SaveLlmSettings(
        [FromBody] SaveUserLlmSettingsCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _llmPreferenceService.SaveSettingsAsync(
                ExtractBearerToken(),
                request,
                cancellationToken).ConfigureAwait(false);
            return Ok(await _llmPreferenceService
                .GetSettingsAsync(ExtractBearerToken(), cancellationToken)
                .ConfigureAwait(false));
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
            var config = await _queryPort.GetAsync(cancellationToken);
            return Ok(new UserConfigRuntimeView(
                RuntimeMode: UserConfigRuntime.NormalizeMode(config.RuntimeMode),
                ActiveRuntimeBaseUrl: UserConfigRuntime.ResolveActiveRuntimeBaseUrl(config),
                LocalRuntimeBaseUrl: UserConfigRuntime.NormalizeBaseUrl(
                    config.LocalRuntimeBaseUrl,
                    UserConfigRuntimeDefaults.LocalRuntimeBaseUrl),
                RemoteRuntimeBaseUrl: UserConfigRuntime.NormalizeBaseUrl(
                    config.RemoteRuntimeBaseUrl,
                    UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl),
                RuntimeDefaults: new UserConfigRuntimeDefaultsView(
                    LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
                    RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl,
                    LocalMode: UserConfigRuntimeDefaults.LocalMode,
                    RemoteMode: UserConfigRuntimeDefaults.RemoteMode)));
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
