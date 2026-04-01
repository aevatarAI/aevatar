using System.ClientModel.Primitives;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;

namespace Aevatar.AI.LLMProviders.NyxId;

/// <summary>
/// NyxID gateway-backed provider. Requests are sent through NyxID's OpenAI-compatible gateway
/// using the logged-in user's NyxID access token, so NyxID resolves that user's own LLM credentials.
/// </summary>
public sealed class NyxIdLLMProvider : ILLMProvider
{
    private readonly string _defaultModel;
    private readonly Uri _nyxEndpoint;
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
        _nyxEndpoint = NormalizeNyxEndpoint(nyxEndpoint);
        _accessTokenAccessor = accessTokenAccessor ?? throw new ArgumentNullException(nameof(accessTokenAccessor));
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name { get; }

    public async Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
    {
        var resolvedModel = ResolveModel(request);
        var accessToken = ResolveAccessToken(request, out _);
        var provider = CreateDelegateProvider(resolvedModel, accessToken);
        return await provider.ChatAsync(NormalizeRequest(request, resolvedModel), ct);
    }

    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var resolvedModel = ResolveModel(request);
        var accessToken = ResolveAccessToken(request, out _);
        var provider = CreateDelegateProvider(resolvedModel, accessToken);

        await foreach (var chunk in provider.ChatStreamAsync(NormalizeRequest(request, resolvedModel), ct))
            yield return chunk;
    }

    private ILLMProvider CreateDelegateProvider(string resolvedModel, string accessToken)
    {
        var options = new OpenAI.OpenAIClientOptions { Endpoint = _nyxEndpoint };
        if (ShouldSuppressDefaultUserAgent(_nyxEndpoint))
            options.Transport = new NyxIdProxyTransport();

        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(accessToken), options);
        var chatClient = client.GetChatClient(resolvedModel).AsIChatClient();
        return new MEAILLMProvider(Name, chatClient, _logger);
    }

    private LLMRequest NormalizeRequest(LLMRequest request, string resolvedModel)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new LLMRequest
        {
            Messages = request.Messages,
            RequestId = request.RequestId,
            Metadata = request.Metadata,
            Tools = request.Tools,
            Model = resolvedModel,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
        };
    }

    private string ResolveModel(LLMRequest request)
    {
        var requestedModel = request.Model?.Trim();
        return string.IsNullOrWhiteSpace(requestedModel)
            ? _defaultModel
            : requestedModel;
    }

    private string ResolveAccessToken(LLMRequest request, out string tokenSource)
    {
        // Priority 1: per-user token from request metadata (user's NyxID login token)
        var userToken = TryGetMetadataValue(request, LLMRequestMetadataKeys.NyxIdAccessToken);
        if (!string.IsNullOrWhiteSpace(userToken))
        {
            tokenSource = "request_metadata";
            return userToken;
        }

        // Priority 2: static/configured token (fallback for background tasks)
        var configuredToken = _accessTokenAccessor()?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            tokenSource = "configured_accessor";
            return configuredToken;
        }

        tokenSource = "missing";

        throw new InvalidOperationException(
            $"NyxID access token is not available for provider '{Name}'. " +
            "Ensure the user is logged in via NyxID.");
    }

    private static string? TryGetMetadataValue(LLMRequest request, string key) =>
        request.Metadata != null && request.Metadata.TryGetValue(key, out var value)
            ? value?.Trim()
            : null;

    private static Uri NormalizeNyxEndpoint(string nyxEndpoint)
    {
        var trimmed = nyxEndpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var endpoint))
            throw new ArgumentException(
                $"NyxID endpoint '{nyxEndpoint}' must be an absolute URI.",
                nameof(nyxEndpoint));

        return new Uri(endpoint.ToString().TrimEnd('/') + "/", UriKind.Absolute);
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
