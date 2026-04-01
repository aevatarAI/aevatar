using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.AI.LLMProviders.NyxId;

/// <summary>
/// NyxID-backed provider. Requests use the logged-in user's NyxID access token and are routed
/// by NyxID account state: prefer a configured chrono-llm service proxy, otherwise fall back
/// to NyxID's OpenAI-compatible LLM gateway.
/// </summary>
public sealed class NyxIdLLMProvider : ILLMProvider
{
    private const string AutoRoute = "auto";
    private const string GatewayRoute = "gateway";
    private const string GatewaySuffix = "/api/v1/llm/gateway/v1/";
    private const string ChronoLlmServiceSlug = "chrono-llm";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _defaultModel;
    private readonly Uri _gatewayEndpoint;
    private readonly Uri _authorityBase;
    private readonly Func<string?> _accessTokenAccessor;
    private readonly HttpClient _routeLookupClient;
    private readonly ILogger _logger;

    public NyxIdLLMProvider(
        string name,
        string defaultModel,
        string gatewayEndpoint,
        Func<string?> accessTokenAccessor,
        ILogger? logger = null,
        HttpClient? routeLookupClient = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Provider name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(defaultModel))
            throw new ArgumentException("Default model is required.", nameof(defaultModel));
        if (string.IsNullOrWhiteSpace(gatewayEndpoint))
            throw new ArgumentException("Gateway endpoint is required.", nameof(gatewayEndpoint));

        Name = name.Trim();
        _defaultModel = defaultModel.Trim();
        _gatewayEndpoint = NormalizeGatewayEndpoint(gatewayEndpoint);
        _authorityBase = ResolveAuthorityBase(_gatewayEndpoint);
        _accessTokenAccessor = accessTokenAccessor ?? throw new ArgumentNullException(nameof(accessTokenAccessor));
        _routeLookupClient = routeLookupClient ?? CreateRouteLookupClient();
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name { get; }

    public async Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
    {
        var route = await ResolveRouteAsync(request, ct);
        return await CreateDelegateProvider(route.Request, route.Endpoint, route.RouteName, route.AccessToken)
            .ChatAsync(route.Request, ct);
    }

    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var route = await ResolveRouteAsync(request, ct);
        await foreach (var chunk in CreateDelegateProvider(route.Request, route.Endpoint, route.RouteName, route.AccessToken)
                           .ChatStreamAsync(route.Request, ct)
                           .WithCancellation(ct))
        {
            yield return chunk;
        }
    }

    internal async Task<NyxIdResolvedRoute> ResolveRouteAsync(LLMRequest request, CancellationToken ct = default)
    {
        var normalizedRequest = NormalizeRequest(request);
        var accessToken = ResolveAccessToken(normalizedRequest);
        var routePreference = NormalizeRoutePreference(
            TryGetMetadataValue(normalizedRequest, LLMRequestMetadataKeys.NyxIdRoutePreference));
        var route = await ResolvePreferredRouteAsync(normalizedRequest, accessToken, routePreference, ct);

        _logger.LogDebug(
            "Resolved NyxID LLM route '{RouteName}' to {Endpoint} for model {Model}",
            route.RouteName,
            route.Endpoint,
            route.Request.Model);

        return route;
    }

    private async Task<NyxIdResolvedRoute> ResolvePreferredRouteAsync(
        LLMRequest request,
        string accessToken,
        string routePreference,
        CancellationToken ct)
    {
        if (string.Equals(routePreference, GatewayRoute, StringComparison.OrdinalIgnoreCase))
            return new NyxIdResolvedRoute(Name, _gatewayEndpoint, request, accessToken);

        if (!string.Equals(routePreference, AutoRoute, StringComparison.OrdinalIgnoreCase))
        {
            var explicitRoute = await TryResolveServiceRouteAsync(request, accessToken, routePreference, ct);
            return explicitRoute ?? new NyxIdResolvedRoute(Name, _gatewayEndpoint, request, accessToken);
        }

        var autoRoute = await TryResolveServiceRouteAsync(request, accessToken, ChronoLlmServiceSlug, ct);
        return autoRoute ?? new NyxIdResolvedRoute(Name, _gatewayEndpoint, request, accessToken);
    }

    private ILLMProvider CreateDelegateProvider(
        LLMRequest request,
        Uri endpoint,
        string routeName,
        string accessToken)
    {
        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = endpoint,
        };
        // Override User-Agent to avoid Cloudflare WAF blocking the default "OpenAI/x.x" agent string.
        options.AddPolicy(new NyxIdUserAgentPolicy(), System.ClientModel.Primitives.PipelinePosition.BeforeTransport);
        options.AddPolicy(new NyxIdRequestLoggingPolicy(), System.ClientModel.Primitives.PipelinePosition.BeforeTransport);
        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(accessToken), options);
        var chatClient = client.GetChatClient(request.Model!).AsIChatClient();
        return new MEAILLMProvider(routeName, chatClient, _logger);
    }

    private LLMRequest NormalizeRequest(LLMRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new LLMRequest
        {
            Messages = request.Messages,
            RequestId = request.RequestId,
            Metadata = request.Metadata,
            Tools = request.Tools,
            Model = ResolveModel(request),
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
        };
    }

    private async Task<NyxIdResolvedRoute?> TryResolveServiceRouteAsync(
        LLMRequest request,
        string accessToken,
        string serviceSlug,
        CancellationToken ct)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(_authorityBase, "api/v1/keys"));
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _routeLookupClient.SendAsync(httpRequest, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "NyxID route lookup returned HTTP {StatusCode}; fallback to gateway",
                    (int)response.StatusCode);
                return null;
            }

            await using var payload = await response.Content.ReadAsStreamAsync(ct);
            var keys = await JsonSerializer.DeserializeAsync<NyxIdUserServicesEnvelope>(payload, JsonOptions, ct);
            var matchedService = keys?.Keys?.FirstOrDefault(service =>
                IsServiceRouteConfigured(service, serviceSlug));
            if (matchedService is null)
                return null;

            var slug = matchedService.Slug!.Trim();
            var endpoint = new Uri(_authorityBase, $"api/v1/proxy/s/{Uri.EscapeDataString(slug)}/");
            return new NyxIdResolvedRoute(slug, endpoint, request, accessToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NyxID route lookup failed; fallback to gateway");
            return null;
        }
    }

    private string ResolveModel(LLMRequest request)
    {
        // Priority 1: per-request model override from metadata (user's configured default model)
        var metadataModel = TryGetMetadataValue(request, LLMRequestMetadataKeys.ModelOverride);
        var requestedModel = request.Model?.Trim();
        var metadataKeyCount = request.Metadata?.Count ?? -1;

        var resolved = !string.IsNullOrWhiteSpace(metadataModel)
            ? metadataModel
            : !string.IsNullOrWhiteSpace(requestedModel)
                ? requestedModel
                : _defaultModel;

        Console.Error.WriteLine(
            $"[NyxIdLLM.ResolveModel] metadataModel={metadataModel ?? "<null>"}, requestModel={requestedModel ?? "<null>"}, default={_defaultModel}, resolved={resolved}, metadataKeys={metadataKeyCount}");

        return resolved;
    }

    private string ResolveAccessToken(LLMRequest request)
    {
        // Priority 1: per-user token from request metadata (user's NyxID login token)
        var userToken = TryGetMetadataValue(request, LLMRequestMetadataKeys.NyxIdAccessToken);
        if (!string.IsNullOrWhiteSpace(userToken))
        {
            Console.Error.WriteLine($"[NyxIdLLM.ResolveAccessToken] Using per-request token (len={userToken.Length})");
            return userToken;
        }

        // Priority 2: static/configured token (fallback for background tasks)
        var configuredToken = _accessTokenAccessor()?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            Console.Error.WriteLine($"[NyxIdLLM.ResolveAccessToken] Using configured/fallback token (len={configuredToken.Length})");
            return configuredToken;
        }

        throw new NyxIdAuthenticationRequiredException(Name);
    }

    private static string? TryGetMetadataValue(LLMRequest request, string key) =>
        request.Metadata != null && request.Metadata.TryGetValue(key, out var value)
            ? value?.Trim()
            : null;

    private static string NormalizeRoutePreference(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized)
            ? AutoRoute
            : normalized;
    }

    private static HttpClient CreateRouteLookupClient() => new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static Uri NormalizeGatewayEndpoint(string gatewayEndpoint)
    {
        var trimmed = gatewayEndpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var endpoint))
            throw new ArgumentException(
                $"NyxID gateway endpoint '{gatewayEndpoint}' must be an absolute URI.",
                nameof(gatewayEndpoint));

        return new Uri(endpoint.ToString().TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static Uri ResolveAuthorityBase(Uri gatewayEndpoint)
    {
        var absolute = gatewayEndpoint.ToString();
        if (absolute.EndsWith(GatewaySuffix, StringComparison.OrdinalIgnoreCase))
            return new Uri(absolute[..^GatewaySuffix.Length] + "/", UriKind.Absolute);

        return new Uri(gatewayEndpoint.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    private static bool IsServiceRouteConfigured(NyxIdUserService service, string serviceSlug) =>
        string.Equals(service.Status?.Trim(), "active", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(service.Slug?.Trim(), serviceSlug, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(service.EndpointUrl);
}

internal sealed record NyxIdResolvedRoute(
    string RouteName,
    Uri Endpoint,
    LLMRequest Request,
    string AccessToken);

internal sealed class NyxIdUserServicesEnvelope
{
    [JsonPropertyName("keys")]
    public List<NyxIdUserService>? Keys { get; set; }
}

internal sealed class NyxIdUserService
{
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("endpoint_url")]
    public string? EndpointUrl { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

/// <summary>
/// Pipeline policy that logs the actual HTTP request URL and response status for debugging.
/// </summary>
/// <summary>
/// Replaces the default OpenAI SDK User-Agent with a neutral one.
/// Cloudflare WAF on NyxID gateway blocks the "OpenAI/x.x" agent string.
/// </summary>
internal sealed class NyxIdUserAgentPolicy : System.ClientModel.Primitives.PipelinePolicy
{
    private const string UserAgent = "Aevatar/1.0";

    public override void Process(System.ClientModel.Primitives.PipelineMessage message, IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline, int currentIndex)
    {
        message.Request.Headers.Set("User-Agent", UserAgent);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(System.ClientModel.Primitives.PipelineMessage message, IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline, int currentIndex)
    {
        message.Request.Headers.Set("User-Agent", UserAgent);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }
}

internal sealed class NyxIdRequestLoggingPolicy : System.ClientModel.Primitives.PipelinePolicy
{
    public override void Process(System.ClientModel.Primitives.PipelineMessage message, IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline, int currentIndex)
    {
        LogRequest(message);
        ProcessNext(message, pipeline, currentIndex);
        LogResponse(message);
    }

    public override async ValueTask ProcessAsync(System.ClientModel.Primitives.PipelineMessage message, IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline, int currentIndex)
    {
        LogRequest(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
        LogResponse(message);
    }

    private static void LogRequest(System.ClientModel.Primitives.PipelineMessage message)
    {
        Console.Error.WriteLine($"[NyxIdLLM.HTTP] {message.Request.Method} {message.Request.Uri}");
        foreach (var header in message.Request.Headers)
        {
            var val = header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? header.Value?[..Math.Min(header.Value.Length, 30)] + "..."
                : header.Value;
            Console.Error.WriteLine($"[NyxIdLLM.HTTP]   {header.Key}: {val}");
        }
    }

    private static void LogResponse(System.ClientModel.Primitives.PipelineMessage message)
    {
        var status = message.Response?.Status;
        Console.Error.WriteLine($"[NyxIdLLM.HTTP] Response: {status}");
        if (status is >= 400)
        {
            try
            {
                using var reader = new StreamReader(message.Response!.ContentStream!, leaveOpen: true);
                var body = reader.ReadToEnd();
                message.Response.ContentStream!.Position = 0;
                Console.Error.WriteLine($"[NyxIdLLM.HTTP] Response body: {body}");
            }
            catch
            {
                // best-effort
            }
        }
    }
}
