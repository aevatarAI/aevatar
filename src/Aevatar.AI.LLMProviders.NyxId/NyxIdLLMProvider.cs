using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.LLMProviders.NyxId;

/// <summary>
/// NyxID gateway-backed provider. Requests are sent through NyxID's OpenAI-compatible gateway
/// using the logged-in user's NyxID access token, so NyxID resolves that user's own LLM credentials.
/// </summary>
public sealed class NyxIdLLMProvider : ILLMProvider
{
    private readonly string _defaultModel;
    private readonly Uri _gatewayEndpoint;
    private readonly Func<string?> _accessTokenAccessor;
    private readonly ILogger _logger;

    public NyxIdLLMProvider(
        string name,
        string defaultModel,
        string gatewayEndpoint,
        Func<string?> accessTokenAccessor,
        ILogger? logger = null)
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
        _accessTokenAccessor = accessTokenAccessor ?? throw new ArgumentNullException(nameof(accessTokenAccessor));
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name { get; }

    public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default) =>
        CreateDelegateProvider(request).ChatAsync(NormalizeRequest(request), ct);

    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(LLMRequest request, CancellationToken ct = default) =>
        CreateDelegateProvider(request).ChatStreamAsync(NormalizeRequest(request), ct);

    private ILLMProvider CreateDelegateProvider(LLMRequest request)
    {
        var resolvedModel = ResolveModel(request);
        var accessToken = ResolveAccessToken(request);

        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = _gatewayEndpoint,
        };
        // Override User-Agent to avoid Cloudflare WAF blocking the default "OpenAI/x.x" agent string.
        options.AddPolicy(new NyxIdUserAgentPolicy(), System.ClientModel.Primitives.PipelinePosition.BeforeTransport);
        options.AddPolicy(new NyxIdRequestLoggingPolicy(), System.ClientModel.Primitives.PipelinePosition.BeforeTransport);
        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential(accessToken), options);
        var chatClient = client.GetChatClient(resolvedModel).AsIChatClient();
        return new MEAILLMProvider(Name, chatClient, _logger);
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

    private static Uri NormalizeGatewayEndpoint(string gatewayEndpoint)
    {
        var trimmed = gatewayEndpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var endpoint))
            throw new ArgumentException(
                $"NyxID gateway endpoint '{gatewayEndpoint}' must be an absolute URI.",
                nameof(gatewayEndpoint));

        return new Uri(endpoint.ToString().TrimEnd('/') + "/", UriKind.Absolute);
    }
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
