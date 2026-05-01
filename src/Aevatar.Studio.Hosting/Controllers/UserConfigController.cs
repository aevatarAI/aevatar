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
    private readonly IUserConfigQueryPort _queryPort;
    private readonly IUserConfigCommandService _commandService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserConfigController> _logger;

    public UserConfigController(
        IUserConfigQueryPort queryPort,
        IUserConfigCommandService commandService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<UserConfigController> logger)
    {
        _queryPort = queryPort;
        _commandService = commandService;
        _httpClientFactory = httpClientFactory;
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
            var options = await LoadLlmOptionsAsync(cancellationToken);
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
            return Ok(await LoadLlmOptionsAsync(cancellationToken));
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
            var current = await _queryPort.GetAsync(cancellationToken);
            UserConfig next;
            if (request.Reset == true)
            {
                next = current with
                {
                    DefaultModel = string.Empty,
                    PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
                };
            }
            else if (!string.IsNullOrWhiteSpace(request.ServiceId))
            {
                var options = await LoadLlmOptionsAsync(cancellationToken);
                var option = FindLlmOption(options.Available, request.ServiceId!);
                if (option is null)
                    return BadRequest(new { message = $"LLM service '{request.ServiceId}' is not routable for this user." });
                if (!IsSelectable(option, out var selectError))
                    return BadRequest(new { message = selectError });

                next = current with
                {
                    PreferredLlmRoute = UserConfigLlmRoute.Normalize(option.RouteValue),
                    DefaultModel = NormalizeOptional(request.Model) ?? option.DefaultModel ?? string.Empty,
                };
            }
            else if (!string.IsNullOrWhiteSpace(request.RouteValue))
            {
                var routeValue = UserConfigLlmRoute.Normalize(request.RouteValue);
                var options = await LoadLlmOptionsAsync(cancellationToken);
                if (string.Equals(routeValue, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase))
                {
                    next = current with
                    {
                        PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
                        DefaultModel = NormalizeOptional(request.Model) ?? current.DefaultModel,
                    };
                }
                else
                {
                    var option = FindLlmOption(options.Available, routeValue);
                    if (option is null)
                        return BadRequest(new { message = $"LLM route '{request.RouteValue}' is not routable for this user." });
                    if (!IsSelectable(option, out var selectError))
                        return BadRequest(new { message = selectError });

                    next = current with
                    {
                        PreferredLlmRoute = UserConfigLlmRoute.Normalize(option.RouteValue),
                        DefaultModel = NormalizeOptional(request.Model) ?? option.DefaultModel ?? string.Empty,
                    };
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.PresetId))
            {
                var activated = await ActivatePresetAsync(request.PresetId!, cancellationToken);
                next = current with
                {
                    PreferredLlmRoute = UserConfigLlmRoute.Normalize(activated.RouteValue),
                    DefaultModel = NormalizeOptional(request.Model) ?? activated.DefaultModel ?? string.Empty,
                };
            }
            else if (request.Model is not null)
            {
                next = current with { DefaultModel = request.Model.Trim() };
            }
            else
            {
                return BadRequest(new { message = "Specify serviceId, presetId, model, or reset." });
            }

            await _commandService.SaveAsync(next, cancellationToken);
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

    private async Task<UserLlmOptionsView> LoadLlmOptionsAsync(CancellationToken cancellationToken)
    {
        var bearerToken = ExtractBearerToken();
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            _logger.LogWarning("No Bearer token found in request for LLM options endpoint");
            return UserLlmOptionsView.Empty;
        }

        var result = await FetchLlmServicesAsync(bearerToken, cancellationToken);
        var config = await _queryPort.GetAsync(cancellationToken);
        var route = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute);
        var current = result.Services.FirstOrDefault(option =>
            string.Equals(option.RouteValue, route, StringComparison.OrdinalIgnoreCase));
        if (current is not null && !string.IsNullOrWhiteSpace(config.DefaultModel))
            current = current with { DefaultModel = config.DefaultModel.Trim() };

        return new UserLlmOptionsView(current, result.Services, result.SetupHint);
    }

    private async Task<LlmServicesResult> FetchLlmServicesAsync(
        string bearerToken,
        CancellationToken cancellationToken)
    {
        var responseBody = await SendNyxIdAsync(
            HttpMethod.Get,
            "/api/v1/llm/services",
            bearerToken,
            body: null,
            cancellationToken).ConfigureAwait(false);
        return ParseLlmServicesResult(responseBody);
    }

    private async Task<UserLlmOption> ActivatePresetAsync(string presetId, CancellationToken cancellationToken)
    {
        var bearerToken = ExtractBearerToken();
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new InvalidOperationException("Bearer token is required to activate an LLM preset.");

        var options = await FetchLlmServicesAsync(bearerToken, cancellationToken);
        var preset = options.SetupHint?.Presets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, presetId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            throw new InvalidOperationException($"LLM preset '{presetId}' is not available.");

        return preset.Activation switch
        {
            UseExistingService existing => ActivateExistingPreset(options.Services, existing),
            ProvisionThenUse provisioning => await ActivateProvisioningPresetAsync(
                bearerToken,
                provisioning.ProvisionEndpointId,
                cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported LLM preset activation for '{preset.Id}'."),
        };
    }

    private static UserLlmOption ActivateExistingPreset(
        IReadOnlyList<UserLlmOption> services,
        UseExistingService existing)
    {
        var option = services.FirstOrDefault(candidate =>
            string.Equals(candidate.ServiceId, existing.ServiceId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.RouteValue, existing.RouteValue, StringComparison.OrdinalIgnoreCase));
        if (option is null)
            throw new InvalidOperationException($"LLM service '{existing.ServiceId}' is not routable for this user.");
        if (!IsSelectable(option, out var selectError))
            throw new InvalidOperationException(selectError);

        return option with { DefaultModel = existing.DefaultModel ?? option.DefaultModel };
    }

    private async Task<UserLlmOption> ActivateProvisioningPresetAsync(
        string bearerToken,
        string provisionEndpointId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provisionEndpointId))
            throw new InvalidOperationException("LLM preset provisioning endpoint is missing.");

        var body = await SendNyxIdAsync(
            HttpMethod.Post,
            $"/api/v1/llm/services/{Uri.EscapeDataString(provisionEndpointId.Trim())}",
            bearerToken,
            "{}",
            cancellationToken);
        var result = ParseProvisionedLlmService(body);
        if (!IsSelectable(result, out var selectError))
            throw new InvalidOperationException(selectError);
        return result;
    }

    private async Task<string> SendNyxIdAsync(
        HttpMethod method,
        string path,
        string bearerToken,
        string? body,
        CancellationToken cancellationToken)
    {
        var authorityBase = ResolveNyxIdAuthorityBase();
        if (string.IsNullOrWhiteSpace(authorityBase))
            throw new InvalidOperationException("NyxID authority is not configured.");

        using var request = new HttpRequestMessage(method, $"{authorityBase}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (body is not null)
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return responseBody;

        _logger.LogWarning(
            "NyxID LLM services endpoint returned {StatusCode}: {Body}",
            response.StatusCode,
            responseBody.Length > 500 ? responseBody[..500] : responseBody);
        throw new InvalidOperationException("NyxID LLM services request failed.");
    }

    private static NyxIdLlmStatusResponse ToLegacyStatusResponse(UserLlmOptionsView options)
    {
        var providers = options.Available.Select(option => new NyxIdLlmProviderStatus
        {
            ProviderSlug = option.ServiceSlug,
            ProviderName = option.DisplayName,
            Status = option.Status,
            ProxyUrl = option.RouteValue,
            Source = option.Source,
        }).ToList();

        var modelsByProvider = options.Available.ToDictionary(
            option => option.ServiceSlug,
            option => option.AvailableModels
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

    private static LlmServicesResult ParseLlmServicesResult(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return new LlmServicesResult([], null);

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
            return new LlmServicesResult(ParseLlmServiceArray(root), null);
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NyxID LLM services response must be a JSON object or array.");

        var servicesElement = TryGetProperty(root, "services", "items");
        var services = servicesElement?.ValueKind == JsonValueKind.Array
            ? ParseLlmServiceArray(servicesElement.Value)
            : [];
        var setupHint = TryGetProperty(root, "setup_hint", "setupHint") is { } hint
            ? ParseSetupHint(hint)
            : null;
        return new LlmServicesResult(services, setupHint);
    }

    private static UserLlmOption ParseProvisionedLlmService(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            throw new InvalidOperationException("NyxID returned an empty provisioned LLM service response.");

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, "service") is { } service)
        {
            return ParseLlmService(service);
        }

        return ParseLlmService(root);
    }

    private static IReadOnlyList<UserLlmOption> ParseLlmServiceArray(JsonElement servicesElement)
    {
        var services = new List<UserLlmOption>();
        foreach (var serviceElement in servicesElement.EnumerateArray())
            services.Add(ParseLlmService(serviceElement));
        return services;
    }

    private static UserLlmOption ParseLlmService(JsonElement serviceElement)
    {
        if (serviceElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NyxID LLM service entry must be a JSON object.");

        var serviceSlug = ReadRequiredString(serviceElement, "service_slug", "serviceSlug");
        var routeValue = ReadRequiredString(serviceElement, "route_value", "routeValue", "proxy_url", "proxyUrl");
        return new UserLlmOption(
            ServiceId: ReadRequiredString(serviceElement, "user_service_id", "userServiceId", "service_id", "serviceId"),
            ServiceSlug: serviceSlug,
            DisplayName: ReadOptionalString(serviceElement, "display_name", "displayName", "service_name", "serviceName") ?? serviceSlug,
            RouteValue: routeValue,
            DefaultModel: ReadOptionalString(serviceElement, "default_model", "defaultModel"),
            AvailableModels: ReadStringArray(serviceElement, "models", "available_models", "availableModels"),
            Status: ReadOptionalString(serviceElement, "status") ?? "unknown",
            Source: ReadOptionalString(serviceElement, "source") ?? NyxIdLlmProviderSource.UserService,
            Allowed: ReadOptionalBool(serviceElement, "allowed") ?? false,
            Description: ReadOptionalString(serviceElement, "description"));
    }

    private static UserLlmSetupHint? ParseSetupHint(JsonElement hintElement)
    {
        if (hintElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (hintElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NyxID LLM setup hint must be a JSON object.");

        var presets = new List<UserLlmPreset>();
        if (TryGetProperty(hintElement, "presets") is { ValueKind: JsonValueKind.Array } presetsElement)
        {
            foreach (var presetElement in presetsElement.EnumerateArray())
                presets.Add(ParsePreset(presetElement));
        }

        return new UserLlmSetupHint(
            SetupUrl: ReadOptionalString(hintElement, "setup_url", "setupUrl") ?? string.Empty,
            Presets: presets);
    }

    private static UserLlmPreset ParsePreset(JsonElement presetElement)
    {
        if (presetElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("NyxID LLM preset entry must be a JSON object.");

        var id = ReadRequiredString(presetElement, "id");
        return new UserLlmPreset(
            Id: id,
            Title: ReadOptionalString(presetElement, "title") ?? id,
            Description: ReadOptionalString(presetElement, "description") ?? string.Empty,
            Activation: ParsePresetActivation(presetElement));
    }

    private static UserLlmPresetActivation ParsePresetActivation(JsonElement presetElement)
    {
        var activationElement = TryGetProperty(presetElement, "activation") is { ValueKind: JsonValueKind.Object } nested
            ? nested
            : presetElement;
        var type = ReadOptionalString(activationElement, "type", "kind")
            ?? ReadOptionalString(presetElement, "activation_type", "activationType")
            ?? string.Empty;
        return type.Trim().ToLowerInvariant() switch
        {
            "use_existing_service" or "use-existing-service" or "existing" => new UseExistingService(
                ServiceId: ReadRequiredString(activationElement, "service_id", "serviceId"),
                RouteValue: ReadRequiredString(activationElement, "route_value", "routeValue"),
                DefaultModel: ReadOptionalString(activationElement, "default_model", "defaultModel")),
            "provision_then_use" or "provision-then-use" or "provision" => new ProvisionThenUse(
                ProvisionEndpointId: ReadRequiredString(
                    activationElement,
                    "provision_endpoint_id",
                    "provisionEndpointId")),
            _ => throw new InvalidOperationException($"Unsupported NyxID LLM preset activation type '{type}'."),
        };
    }

    private static UserLlmOption? FindLlmOption(IReadOnlyList<UserLlmOption> options, string requested)
    {
        var normalized = requested.Trim();
        return options.FirstOrDefault(option =>
            string.Equals(option.ServiceId, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.ServiceSlug, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.DisplayName, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.RouteValue, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSelectable(UserLlmOption option, out string error)
    {
        error = string.Empty;
        if (!option.Allowed)
        {
            error = $"LLM service '{option.DisplayName}' is not allowed for this user.";
            return false;
        }

        if (!string.Equals(option.Status, "ready", StringComparison.OrdinalIgnoreCase))
        {
            error = $"LLM service '{option.DisplayName}' is not ready: {option.Status}.";
            return false;
        }

        return true;
    }

    private static JsonElement? TryGetProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property))
                return property;
        }

        return null;
    }

    private static string ReadRequiredString(JsonElement element, params string[] propertyNames)
    {
        var value = ReadOptionalString(element, propertyNames);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"NyxID LLM response is missing required field '{propertyNames[0]}'.");
        return value.Trim();
    }

    private static string? ReadOptionalString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static bool? ReadOptionalBool(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;
            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return [];
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

public static class NyxIdLlmProviderSource
{
    public const string GatewayProvider = "gateway_provider";
    public const string UserService = "user_service";
}

public sealed record UserLlmOptionsView(
    [property: JsonPropertyName("current")] UserLlmOption? Current,
    [property: JsonPropertyName("available")] IReadOnlyList<UserLlmOption> Available,
    [property: JsonPropertyName("setupHint")] UserLlmSetupHint? SetupHint)
{
    public static readonly UserLlmOptionsView Empty = new(null, [], null);
}

public sealed record UserLlmOption(
    [property: JsonPropertyName("serviceId")] string ServiceId,
    [property: JsonPropertyName("serviceSlug")] string ServiceSlug,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("routeValue")] string RouteValue,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel,
    [property: JsonPropertyName("availableModels")] IReadOnlyList<string> AvailableModels,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("description")] string? Description);

public sealed record UserLlmSetupHint(
    [property: JsonPropertyName("setupUrl")] string SetupUrl,
    [property: JsonPropertyName("presets")] IReadOnlyList<UserLlmPreset> Presets);

public sealed record UserLlmPreset(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("activation")] UserLlmPresetActivation Activation);

public abstract record UserLlmPresetActivation([property: JsonPropertyName("type")] string Type);

public sealed record UseExistingService(
    [property: JsonPropertyName("serviceId")] string ServiceId,
    [property: JsonPropertyName("routeValue")] string RouteValue,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel)
    : UserLlmPresetActivation("use_existing_service");

public sealed record ProvisionThenUse(
    [property: JsonPropertyName("provisionEndpointId")] string ProvisionEndpointId)
    : UserLlmPresetActivation("provision_then_use");

internal sealed record LlmServicesResult(
    IReadOnlyList<UserLlmOption> Services,
    UserLlmSetupHint? SetupHint);
