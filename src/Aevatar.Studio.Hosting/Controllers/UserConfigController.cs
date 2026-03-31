using System.Net.Http.Headers;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IUserConfigStore _userConfigStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserConfigController> _logger;

    public UserConfigController(
        IUserConfigStore userConfigStore,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<UserConfigController> logger)
    {
        _userConfigStore = userConfigStore;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
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

    [HttpGet("models")]
    public async Task<ActionResult<NyxIdLlmStatusResponse>> GetModels(CancellationToken cancellationToken)
    {
        var authorityBase = ResolveNyxIdAuthorityBase();
        if (string.IsNullOrWhiteSpace(authorityBase))
        {
            _logger.LogWarning("NyxID authority not configured; checked keys: Cli:App:NyxId:Authority, Aevatar:NyxId:Authority, Aevatar:Authentication:Authority");
            return Ok(NyxIdLlmStatusResponse.Empty);
        }

        var bearerToken = ExtractBearerToken();
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            _logger.LogWarning("No Bearer token found in request for models endpoint");
            return Ok(NyxIdLlmStatusResponse.Empty);
        }

        try
        {
            var statusUrl = $"{authorityBase}/api/v1/llm/status";
            _logger.LogDebug("Fetching LLM status from NyxID: {Url}", statusUrl);

            using var request = new HttpRequestMessage(HttpMethod.Get, statusUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "NyxID LLM status endpoint returned {StatusCode}: {Body}",
                    response.StatusCode,
                    body.Length > 500 ? body[..500] : body);
                return Ok(NyxIdLlmStatusResponse.Empty);
            }

            var status = JsonSerializer.Deserialize<NyxIdLlmStatusResponse>(body, JsonOptions);
            if (status == null)
                return Ok(NyxIdLlmStatusResponse.Empty);

            // Fetch actual model names from each ready LLM provider via proxy
            var readyProviders = (status.Providers ?? [])
                .Where(p => string.Equals(p.Status, "ready", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Also fetch user's AI Services (custom endpoints), excluding known non-LLM services
            var userServices = await FetchUserServicesAsync(authorityBase, bearerToken, cancellationToken);
            userServices = userServices.Where(s => !IsNonLlmService(s.Slug)).ToList();
            foreach (var svc in userServices)
            {
                // Add as provider for display
                status.Providers ??= [];
                status.Providers.Add(new NyxIdLlmProviderStatus
                {
                    ProviderSlug = svc.Slug,
                    ProviderName = svc.Label,
                    Status = "ready",
                    ProxyUrl = $"{authorityBase}/api/v1/proxy/s/{Uri.EscapeDataString(svc.Slug)}",
                });
                readyProviders.Add(new NyxIdLlmProviderStatus
                {
                    ProviderSlug = svc.Slug,
                    ProviderName = svc.Label,
                    Status = "ready",
                    ProxyUrl = $"{authorityBase}/api/v1/proxy/s/{Uri.EscapeDataString(svc.Slug)}",
                });
            }

            status.SupportedModels = await FetchModelsFromProvidersAsync(
                authorityBase, bearerToken, readyProviders, cancellationToken);

            _logger.LogInformation(
                "Fetched LLM status: {ProviderCount} providers, {ModelCount} models from live providers",
                status.Providers?.Count ?? 0,
                status.SupportedModels?.Count ?? 0);
            return Ok(status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch LLM status from NyxID");
            return Ok(NyxIdLlmStatusResponse.Empty);
        }
    }

    private async Task<List<NyxIdUserService>> FetchUserServicesAsync(
        string authorityBase,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var keysUrl = $"{authorityBase}/api/v1/keys";
            using var request = new HttpRequestMessage(HttpMethod.Get, keysUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var envelope = JsonSerializer.Deserialize<NyxIdKeysResponse>(body, JsonOptions);
            return (envelope?.Keys ?? [])
                .Where(k => string.Equals(k.Status, "active", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(k.Slug)
                    && !string.IsNullOrWhiteSpace(k.EndpointUrl))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch user AI services from NyxID");
            return [];
        }
    }

    /// <summary>
    /// Fallback model names for providers whose /models endpoint is non-standard.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> FallbackModels =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = [
                "claude-sonnet-4-5-20250929",
                "claude-opus-4-20250514",
                "claude-sonnet-4-20250514",
                "claude-haiku-4-5-20251001",
            ],
            ["google-ai"] = [
                "gemini-2.5-pro-preview-06-05",
                "gemini-2.5-flash-preview-05-20",
                "gemini-2.0-flash",
            ],
            ["cohere"] = [
                "command-r-plus", "command-r", "command-a-03-2025",
            ],
        };

    private async Task<List<string>> FetchModelsFromProvidersAsync(
        string authorityBase,
        string bearerToken,
        List<NyxIdLlmProviderStatus> readyProviders,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var tasks = readyProviders
            .Where(p => !string.IsNullOrWhiteSpace(p.ProxyUrl))
            .Select(async provider =>
            {
                var slug = provider.ProviderSlug ?? string.Empty;
                try
                {
                    var proxyUrl = provider.ProxyUrl!.Trim().TrimEnd('/');
                    var baseUrl = proxyUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? proxyUrl
                        : $"{authorityBase}{proxyUrl}";

                    var modelsUrl = $"{baseUrl}/models";
                    using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

                    using var response = await client.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        var envelope = JsonSerializer.Deserialize<OpenAIModelsResponse>(body, JsonOptions);
                        var models = (envelope?.Data ?? [])
                            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                            .Select(m => m.Id!.Trim())
                            .ToArray();

                        if (models.Length > 0)
                            return models;
                    }

                    _logger.LogDebug("Provider {Slug} /models returned {Status}, using fallback", slug, response.StatusCode);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Failed to fetch models for provider {Slug}, using fallback", slug);
                }

                // Fallback for providers with non-standard /models endpoint
                return FallbackModels.TryGetValue(slug, out var fallback) ? fallback : Array.Empty<string>();
            });

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Slug keywords that indicate a non-LLM infrastructure service.
    /// </summary>
    private static readonly string[] NonLlmServiceKeywords =
    [
        "sisyphus", "chrono-storage", "chrono-sandbox", "chrono-graph",
        "ornn", "admin", "webhook", "n8n", "grafana", "prometheus",
    ];

    private static bool IsNonLlmService(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return true;

        var lower = slug.ToLowerInvariant();
        return NonLlmServiceKeywords.Any(kw => lower.Contains(kw, StringComparison.Ordinal));
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
}

internal sealed class NyxIdKeysResponse
{
    [JsonPropertyName("keys")]
    public List<NyxIdUserService>? Keys { get; set; }
}

internal sealed class NyxIdUserService
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("endpoint_url")]
    public string? EndpointUrl { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

internal sealed class OpenAIModelsResponse
{
    [JsonPropertyName("data")]
    public List<OpenAIModelEntry>? Data { get; set; }
}

internal sealed class OpenAIModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
