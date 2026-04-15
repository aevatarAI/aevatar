using System.ClientModel.Primitives;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
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

    private readonly string _defaultModel;
    private readonly Uri _defaultNyxEndpoint;
    private readonly Uri _authorityBase;
    private readonly Func<string?> _accessTokenAccessor;
    private readonly ILogger _logger;

    public NyxIdLLMProvider(
        string name,
        string defaultModel,
        string nyxEndpoint,
        Func<string?> accessTokenAccessor,
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
        _authorityBase = ResolveAuthorityBase(_defaultNyxEndpoint);
        _accessTokenAccessor = accessTokenAccessor ?? throw new ArgumentNullException(nameof(accessTokenAccessor));
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name { get; }

    public async Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
    {
        var route = await ResolveRouteAsync(request, ct);
        return await CreateDelegateProvider(route.Request, route.Endpoint, route.RouteName, route.AccessToken)
            .ChatAsync(route.Request, ct);
    }

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
                    string? body = null;
                    int? status = null;
                    for (var cur = ex; cur != null; cur = cur.InnerException)
                    {
                        if (cur is System.ClientModel.ClientResultException cre)
                        {
                            status = cre.Status;
                            var raw = cre.GetRawResponse();
                            if (raw != null)
                                body = System.Text.Encoding.UTF8.GetString(raw.Content.ToArray());
                            break;
                        }
                    }

                    _logger?.LogWarning(ex,
                        "NyxID LLM error: status={Status}, route={Route}, endpoint={Endpoint}, body={Body}",
                        status, route.RouteName, route.Endpoint, body);

                    var detail = $"{ex.Message} | endpoint={route.Endpoint}, model={route.Request.Model}, route={route.RouteName}";
                    if (!string.IsNullOrWhiteSpace(body))
                        detail += $" | NyxID response: {body}";
                    throw new InvalidOperationException(detail, ex);
                }

                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    internal Task<NyxIdResolvedRoute> ResolveRouteAsync(LLMRequest request, CancellationToken ct = default)
    {
        _ = ct;
        var normalizedRequest = NormalizeRequest(request);
        var accessToken = ResolveAccessToken(normalizedRequest);
        var routePreference = NormalizeRoutePreference(
            TryGetMetadataValue(normalizedRequest, LLMRequestMetadataKeys.NyxIdRoutePreference));
        var route = ResolvePreferredRoute(normalizedRequest, accessToken, routePreference);

        _logger.LogDebug(
            "Resolved NyxID LLM route '{RouteName}' to {Endpoint} for model {Model}",
            route.RouteName,
            route.Endpoint,
            route.Request.Model);

        return Task.FromResult(route);
    }

    private NyxIdResolvedRoute ResolvePreferredRoute(
        LLMRequest request,
        string accessToken,
        string routePreference)
    {
        if (string.IsNullOrWhiteSpace(routePreference))
            return new NyxIdResolvedRoute(Name, _defaultNyxEndpoint, request, accessToken);

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

        return new LLMRequest
        {
            Messages = request.Messages,
            RequestId = request.RequestId,
            Metadata = request.Metadata,
            Tools = request.Tools,
            Model = ResolveModel(request),
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            ResponseFormat = request.ResponseFormat,
        };
    }

    private string ResolveModel(LLMRequest request)
    {
        var metadataModel = TryGetMetadataValue(request, LLMRequestMetadataKeys.ModelOverride);
        var requestedModel = request.Model?.Trim();

        return !string.IsNullOrWhiteSpace(metadataModel)
            ? metadataModel
            : !string.IsNullOrWhiteSpace(requestedModel)
                ? requestedModel
                : _defaultModel;
    }

    private string ResolveAccessToken(LLMRequest request)
    {
        var userToken = TryGetMetadataValue(request, LLMRequestMetadataKeys.NyxIdAccessToken);
        if (!string.IsNullOrWhiteSpace(userToken))
            return userToken;

        var configuredToken = _accessTokenAccessor()?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredToken))
            return configuredToken;

        throw new NyxIdAuthenticationRequiredException(Name);
    }

    private static string? TryGetMetadataValue(LLMRequest request, string key) =>
        request.Metadata != null && request.Metadata.TryGetValue(key, out var value)
            ? value?.Trim()
            : null;

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
