using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserConfigController> _logger;

    public UserConfigController(
        IUserConfigQueryPort queryPort,
        IUserConfigCommandService commandService,
        IUserLlmPreferenceService llmPreferenceService,
        IConfiguration configuration,
        ILogger<UserConfigController> logger)
    {
        _queryPort = queryPort;
        _commandService = commandService;
        _llmPreferenceService = llmPreferenceService;
        _configuration = configuration;
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

    [HttpGet("models")]
    public async Task<ActionResult<NyxIdLlmStatusResponse>> GetModels(CancellationToken cancellationToken)
    {
        try
        {
            var options = await _llmPreferenceService
                .GetOptionsAsync(ExtractBearerToken(), cancellationToken)
                .ConfigureAwait(false);
            var status = ToLegacyStatusResponse(options);
            status.GatewayUrl = ResolveNyxIdAuthorityBase() is { } authorityBase
                ? $"{authorityBase}/api/v1/llm/gateway/v1"
                : null;
            status.SupportedModels = (status.ModelsByProvider ?? [])
                .Values
                .SelectMany(models => models)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Ok(status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch LLM services from NyxID");
            return Ok(NyxIdLlmStatusResponse.Empty);
        }
    }

    [HttpGet("llm/options")]
    public async Task<ActionResult<UserLlmOptionsView>> GetLlmOptions(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _llmPreferenceService
                .GetOptionsAsync(ExtractBearerToken(), cancellationToken)
                .ConfigureAwait(false));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to fetch user LLM options");
            return StatusCode(502, new { message = "LLM options are temporarily unavailable." });
        }
    }

    [HttpPut("llm/preference")]
    public async Task<ActionResult<UserConfig>> SaveLlmPreference(
        [FromBody] SaveUserLlmPreferenceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var next = await _llmPreferenceService.SaveAsync(
                ExtractBearerToken(),
                new SaveUserLlmPreferenceCommand(
                    request.ServiceId,
                    request.RouteValue,
                    request.Model,
                    request.PresetId,
                    request.Reset),
                cancellationToken).ConfigureAwait(false);
            return Ok(next);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to save user LLM preference");
            return StatusCode(502, new { message = "User LLM preference is temporarily unavailable." });
        }
    }

    public sealed record SaveUserLlmPreferenceRequest(
        [property: JsonPropertyName("serviceId")] string? ServiceId = null,
        [property: JsonPropertyName("routeValue")] string? RouteValue = null,
        [property: JsonPropertyName("model")] string? Model = null,
        [property: JsonPropertyName("presetId")] string? PresetId = null,
        [property: JsonPropertyName("reset")] bool? Reset = null);

    private static NyxIdLlmStatusResponse ToLegacyStatusResponse(UserLlmOptionsView options)
    {
        var providers = options.Available
            .GroupBy(option => option.ServiceSlug, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new NyxIdLlmProviderStatus
                {
                    ProviderSlug = first.ServiceSlug,
                    ProviderName = first.DisplayName,
                    Status = first.Status,
                    ProxyUrl = first.RouteValue,
                    Source = first.Source,
                };
            })
            .ToList();

        var modelsByProvider = options.Available
            .GroupBy(option => option.ServiceSlug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(option => option.AvailableModels)
                    .Where(model => !string.IsNullOrWhiteSpace(model))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        return new NyxIdLlmStatusResponse
        {
            Providers = providers,
            ModelsByProvider = modelsByProvider,
        };
    }

    private string? ResolveNyxIdAuthorityBase()
    {
        var authority = _configuration["Cli:App:NyxId:Authority"]
            ?? _configuration["Aevatar:NyxId:Authority"]
            ?? _configuration["Aevatar:Authentication:Authority"];

        if (string.IsNullOrWhiteSpace(authority))
            return null;

        var trimmed = authority.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            return null;

        // Strip /api/v1/llm/gateway/v1 suffix if present to get the authority base
        const string gatewaySuffix = "/api/v1/llm/gateway/v1";
        if (trimmed.EndsWith(gatewaySuffix, StringComparison.OrdinalIgnoreCase))
            return trimmed[..^gatewaySuffix.Length];

        return trimmed;
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

public sealed class NyxIdLlmStatusResponse
{
    public static readonly NyxIdLlmStatusResponse Empty = new();

    [JsonPropertyName("providers")]
    public List<NyxIdLlmProviderStatus>? Providers { get; set; }

    [JsonPropertyName("gateway_url")]
    public string? GatewayUrl { get; set; }

    [JsonPropertyName("supported_models")]
    public List<string>? SupportedModels { get; set; }

    [JsonPropertyName("models_by_provider")]
    public Dictionary<string, List<string>>? ModelsByProvider { get; set; }
}

public sealed class NyxIdLlmProviderStatus
{
    [JsonPropertyName("provider_slug")]
    public string? ProviderSlug { get; set; }

    [JsonPropertyName("provider_name")]
    public string? ProviderName { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("proxy_url")]
    public string? ProxyUrl { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}
