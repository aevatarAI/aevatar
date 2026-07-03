using System.ClientModel.Primitives;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.Foundation.Abstractions.Helpers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.LLMProviders.NyxId;

/// <summary>
/// NyxID-backed provider. Requests use the logged-in user's NyxID access token and can override
/// the configured Nyx endpoint with a per-request relative route under the same authority base.
/// </summary>
public sealed class NyxIdLLMProvider : ILLMProvider
{
    private const string GatewaySuffix = "/api/v1/llm/gateway/v1/";
    private static readonly LLMProviderCapabilities ProviderCapabilities = new()
    {
        SupportedInputModalities = new HashSet<ContentPartKind>
        {
            ContentPartKind.Text,
            ContentPartKind.Image,
            ContentPartKind.Audio,
            ContentPartKind.Video,
        },
        SupportedOutputModalities = new HashSet<ContentPartKind>
        {
            ContentPartKind.Text,
            ContentPartKind.Image,
            ContentPartKind.Audio,
            ContentPartKind.Video,
        },
        SupportsStreaming = true,
        SupportsToolCalls = true,
        SupportsReasoningDeltas = true,
    };

    private readonly string _defaultModel;
    private readonly Uri _defaultNyxEndpoint;
    private readonly string _defaultRoutePreference;
    private readonly Uri _authorityBase;
    private readonly Func<string?> _accessTokenAccessor;
    private readonly ILogger _logger;

    public NyxIdLLMProvider(
        string name,
        string defaultModel,
        string nyxEndpoint,
        Func<string?> accessTokenAccessor,
        string? defaultRoutePreference = null,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Provider name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(defaultModel))
            throw new ArgumentException("Default model is required.", nameof(defaultModel));
        if (string.IsNullOrWhiteSpace(nyxEndpoint))
            throw new ArgumentException("Nyx endpoint is required.", nameof(nyxEndpoint));

        Name = name.Trim();
        _defaultModel = defaultModel.Trim();
        _defaultNyxEndpoint = NormalizeNyxEndpoint(nyxEndpoint);
        // When a request carries no route override (the server-default path nulls it), prefer
        // this configured default route over the bare NyxID gateway. The gateway forwards to the
        // OpenAI provider, which fails closed for deployments that have not connected OpenAI;
        // the default route (e.g. the public chrono-llm route) is the connected fallback.
        _defaultRoutePreference = NormalizeRoutePreference(defaultRoutePreference);
        _authorityBase = ResolveAuthorityBase(_defaultNyxEndpoint);
        _accessTokenAccessor = accessTokenAccessor ?? throw new ArgumentNullException(nameof(accessTokenAccessor));
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name { get; }

    public LLMProviderCapabilities Capabilities => ProviderCapabilities;

    // Refactor (iter18/cluster-001):
    //   Old pattern: ILLMProvider 仍暴露 ChatAsync 非流式入口,provider/failover 可绕过流式链路
    //   New principle: Provider contract 只暴露 ChatStreamAsync;非流式聚合用现有 ChatStreamContentAggregator;无新 offline adapter
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        CancellationToken ct = default)
    {
        return ChatStreamCoreAsync(request, ct);
    }

    private async IAsyncEnumerable<LLMStreamChunk> ChatStreamCoreAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var route = await ResolveRouteAsync(request, ct);
        var provider = CreateDelegateProvider(route.Request, route.Endpoint, route.RouteName, route.AccessToken);

        await foreach (var chunk in EnrichErrors(provider.ChatStreamAsync(route.Request, ct), route).WithCancellation(ct))
            yield return chunk;
    }

    private async IAsyncEnumerable<LLMStreamChunk> EnrichErrors(
        IAsyncEnumerable<LLMStreamChunk> inner,
        NyxIdResolvedRoute route,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var enumerator = inner.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                LLMStreamChunk current;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        yield break;
                    current = enumerator.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw TranslateUpstreamFailure(ex, route);
                }

                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private NyxIdUpstreamException TranslateUpstreamFailure(Exception ex, NyxIdResolvedRoute route)
    {
        var (status, body) = ExtractUpstreamStatusAndBody(ex);

        // The upstream body is relayed verbatim from NyxID's downstream provider; scrub
        // secret-shaped content before logging so a misbehaving upstream cannot echo a
        // submitted secret into our logs. Classification below keeps the raw body — it only
        // extracts a short human-facing summary, never persisted as a credential surface.
        _logger?.LogWarning(ex,
            "NyxID LLM error: status={Status}, route={Route}, endpoint={Endpoint}, body={Body}",
            status, route.RouteName, route.Endpoint, SecretScrubber.Scrub(body));

        return ClassifyUpstreamFailure(ex, status, body, route);
    }

    internal static (int? Status, string? Body) ExtractUpstreamStatusAndBody(Exception ex)
    {
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is System.ClientModel.ClientResultException cre)
            {
                var raw = cre.GetRawResponse();
                var body = raw != null
                    ? System.Text.Encoding.UTF8.GetString(raw.Content.ToArray())
                    : null;
                return (cre.Status, body);
            }
        }

        return (null, null);
    }

    internal static NyxIdUpstreamException ClassifyUpstreamFailure(
        Exception source,
        int? status,
        string? body,
        NyxIdResolvedRoute route)
    {
        var model = route.Request.Model;
        var routeName = route.RouteName;
        var upstreamSummary = ExtractUpstreamSummary(body);

        var (kind, message) = status switch
        {
            503 => (
                NyxIdUpstreamFailureKind.ServiceUnavailable,
                $"Upstream LLM route '{routeName}' is temporarily unavailable (HTTP 503) for model '{model}'. "
                + AppendUpstreamDetail(
                    "Please retry shortly or select a different route/model.",
                    upstreamSummary)),
            429 => (
                NyxIdUpstreamFailureKind.RateLimited,
                $"Upstream LLM route '{routeName}' is rate limited (HTTP 429) for model '{model}'. "
                + AppendUpstreamDetail(
                    "Wait a moment before retrying.",
                    upstreamSummary)),
            401 or 403 => (
                NyxIdUpstreamFailureKind.AuthenticationFailed,
                $"Upstream LLM route '{routeName}' rejected the request with HTTP {status} for model '{model}'. "
                + AppendUpstreamDetail(
                    "Your session may have expired — try signing in again.",
                    upstreamSummary)),
            >= 500 => (
                NyxIdUpstreamFailureKind.UpstreamServerError,
                $"Upstream LLM route '{routeName}' returned server error HTTP {status} for model '{model}'. "
                + AppendUpstreamDetail(
                    "The upstream service is unhealthy — retry later or switch routes.",
                    upstreamSummary)),
            >= 400 => (
                NyxIdUpstreamFailureKind.RequestRejected,
                $"Upstream LLM route '{routeName}' rejected the request with HTTP {status} for model '{model}'. "
                + AppendUpstreamDetail(
                    "Check the model id and route configuration.",
                    upstreamSummary)),
            _ => (
                NyxIdUpstreamFailureKind.ProviderError,
                $"Provider error calling NyxID route '{routeName}' for model '{model}': "
                + AppendUpstreamDetail(
                    SummarizeSourceMessage(source),
                    upstreamSummary)),
        };

        return new NyxIdUpstreamException(kind, status, routeName, model, message, source);
    }

    private static string AppendUpstreamDetail(string message, string? upstreamSummary)
    {
        if (string.IsNullOrWhiteSpace(upstreamSummary))
            return message;

        return $"{message} Upstream said: {upstreamSummary}";
    }

    private static string SummarizeSourceMessage(Exception source)
    {
        var raw = source.Message?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return $"{source.GetType().Name} thrown without a message.";

        var collapsed = raw
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return collapsed.Length > 500 ? collapsed[..500] + "…" : collapsed;
    }

    private static string? ExtractUpstreamSummary(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == System.Text.Json.JsonValueKind.Object
                && error.TryGetProperty("message", out var messageEl)
                && messageEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var upstream = messageEl.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(upstream) ? null : upstream;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Body wasn't valid JSON; fall through to raw trimming below.
        }

        var trimmed = body.Trim();
        return trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
    }

    internal Task<NyxIdResolvedRoute> ResolveRouteAsync(LLMRequest request, CancellationToken ct = default)
    {
        _ = ct;
        var normalizedRequest = NormalizeRequest(request);
        var (accessToken, tokenSource) = ResolveAccessTokenWithSource(normalizedRequest);
        var routePreference = NormalizeRoutePreference(ResolveRoutePreference(normalizedRequest));
        var route = ResolvePreferredRoute(normalizedRequest, accessToken, routePreference);

        // Credential-source probe for the 2026-06-12 empty-reply incident: source + length only.
        _logger.LogInformation(
            "Resolved NyxID LLM route '{RouteName}' to {Endpoint} for model {Model}: tokenSource={TokenSource} tokenLength={TokenLength}",
            route.RouteName,
            route.Endpoint,
            route.Request.Model,
            tokenSource,
            accessToken.Length);

        return Task.FromResult(route);
    }

    private NyxIdResolvedRoute ResolvePreferredRoute(
        LLMRequest request,
        string accessToken,
        string routePreference)
    {
        if (string.IsNullOrWhiteSpace(routePreference))
        {
            if (!string.IsNullOrWhiteSpace(_defaultRoutePreference))
            {
                var defaultEndpoint = new Uri(_authorityBase, _defaultRoutePreference.TrimStart('/'));
                return new NyxIdResolvedRoute(_defaultRoutePreference, defaultEndpoint, request, accessToken);
            }

            return new NyxIdResolvedRoute(Name, _defaultNyxEndpoint, request, accessToken);
        }

        var endpoint = new Uri(_authorityBase, routePreference.TrimStart('/'));
        return new NyxIdResolvedRoute(routePreference, endpoint, request, accessToken);
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

        // Always suppress the OpenAI SDK default User-Agent (e.g. "openai-dotnet/2.0.0")
        // for all NyxID routes. The gateway and proxy both reject requests that look like
        // direct OpenAI SDK connections.
        options.Transport = new NyxIdProxyTransport();

        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(accessToken), options);
        var chatClient = client.GetChatClient(request.Model!).AsIChatClient();
        return new MEAILLMProvider(routeName, chatClient, _logger);
    }

    private LLMRequest NormalizeRequest(LLMRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = ResolveModel(request);

        return new LLMRequest
        {
            Messages = request.Messages,
            RequestId = request.RequestId,
            Metadata = request.Metadata,
            CallerContext = request.CallerContext,
            ToolContext = request.ToolContext,
            RoutingContext = request.RoutingContext,
            LlmControl = request.LlmControl,
            Tools = request.Tools,
            Model = model,
            Temperature = NormalizeTemperatureForModel(model, request.Temperature),
            MaxTokens = request.MaxTokens,
            ResponseFormat = request.ResponseFormat,
        };
    }

    internal static double? NormalizeTemperatureForModel(string? model, double? temperature)
    {
        if (!temperature.HasValue)
            return null;

        // NyxID's current OpenAI-compatible reasoning routes reject the temperature parameter.
        return IsReasoningModel(model) ? null : temperature;
    }

    private static bool IsReasoningModel(string? model)
    {
        var normalized = model?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var slashIndex = normalized.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < normalized.Length - 1)
            normalized = normalized[(slashIndex + 1)..];

        if (normalized.StartsWith("gpt-5-chat", StringComparison.OrdinalIgnoreCase))
            return false;

        return normalized.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || IsOpenAIReasoningFamily(normalized, "o1")
            || IsOpenAIReasoningFamily(normalized, "o3")
            || IsOpenAIReasoningFamily(normalized, "o4");
    }

    private static bool IsOpenAIReasoningFamily(string model, string family)
    {
        return model.Equals(family, StringComparison.OrdinalIgnoreCase)
            || model.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveModel(LLMRequest request)
    {
        var metadataModel = request.LlmControl?.ModelOverride
                            ?? request.RoutingContext?.ModelOverride;
        var requestedModel = request.Model?.Trim();

        return !string.IsNullOrWhiteSpace(metadataModel)
            ? metadataModel
            : !string.IsNullOrWhiteSpace(requestedModel)
                ? requestedModel
                : _defaultModel;
    }

    private string ResolveAccessToken(LLMRequest request)
    {
        var (token, _) = ResolveAccessTokenWithSource(request);
        return token;
    }

    private (string Token, string Source) ResolveAccessTokenWithSource(LLMRequest request)
    {
        var typedToken = request.CallerContext?.Credentials?.NyxIdBearer?.Trim();
        if (!string.IsNullOrWhiteSpace(typedToken))
            return (typedToken, "caller-typed");

        var controlToken = request.LlmControl?.NyxIdAccessToken?.Trim();
        if (!string.IsNullOrWhiteSpace(controlToken))
            return (controlToken, "llm-control");

        var configuredToken = _accessTokenAccessor()?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredToken))
            return (configuredToken, "host-accessor");

        throw new NyxIdAuthenticationRequiredException(Name);
    }

    private static string? ResolveRoutePreference(LLMRequest request) =>
        request.LlmControl?.NyxIdRoutePreference
        ?? request.RoutingContext?.NyxIdRoutePreference;

    private static string NormalizeRoutePreference(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "gateway", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (normalized.StartsWith("/", StringComparison.Ordinal))
            return normalized;

        if (normalized.StartsWith("//", StringComparison.Ordinal) ||
            normalized.Contains("://", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return $"/api/v1/proxy/s/{normalized.Trim('/')}";
    }

    private static Uri NormalizeNyxEndpoint(string nyxEndpoint)
    {
        var trimmed = nyxEndpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var endpoint))
            throw new ArgumentException(
                $"NyxID endpoint '{nyxEndpoint}' must be an absolute URI.",
                nameof(nyxEndpoint));

        return new Uri(endpoint.ToString().TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static Uri ResolveAuthorityBase(Uri nyxEndpoint)
    {
        var absolute = nyxEndpoint.ToString();
        if (absolute.EndsWith(GatewaySuffix, StringComparison.OrdinalIgnoreCase))
            return new Uri(absolute[..^GatewaySuffix.Length] + "/", UriKind.Absolute);

        return new Uri(nyxEndpoint.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    private static bool ShouldSuppressDefaultUserAgent(Uri endpoint) =>
        endpoint.AbsolutePath.StartsWith("/api/v1/proxy/", StringComparison.OrdinalIgnoreCase);

    private sealed class NyxIdProxyTransport : HttpClientPipelineTransport
    {
        protected override void OnSendingRequest(PipelineMessage message, HttpRequestMessage httpRequest)
        {
            httpRequest.Headers.UserAgent.Clear();
            base.OnSendingRequest(message, httpRequest);
        }
    }
}

internal sealed record NyxIdResolvedRoute(
    string RouteName,
    Uri Endpoint,
    LLMRequest Request,
    string AccessToken);
