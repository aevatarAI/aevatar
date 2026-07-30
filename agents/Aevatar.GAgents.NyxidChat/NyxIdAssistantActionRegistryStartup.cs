using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgents.NyxidChat;

internal interface INyxIdAssistantActionRegistrySource
{
    Task<string> FetchAsync(CancellationToken ct);
}

/// <summary>
/// One-shot process configuration initialized before the Host accepts work.
/// This is an immutable external contract snapshot, not conversation or action
/// runtime state, and intentionally exposes no refresh or replacement API.
/// </summary>
internal sealed class NyxIdAssistantActionRegistrySnapshot
{
    private NyxIdAssistantActionRegistry? _registry;

    public void Initialize(NyxIdAssistantActionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (Interlocked.CompareExchange(ref _registry, registry, null) is not null)
        {
            throw new InvalidOperationException(
                "The NyxID Assistant action registry startup snapshot is already initialized.");
        }
    }

    public NyxIdAssistantActionRegistry GetRequired() =>
        Volatile.Read(ref _registry) ?? throw new InvalidOperationException(
            "The NyxID Assistant action registry startup snapshot is not initialized.");
}

internal sealed class NyxIdAssistantActionRegistryHttpSource
    : INyxIdAssistantActionRegistrySource
{
    internal const string HttpClientName = "NyxIdAssistantActionRegistry";
    internal const string RegistryPath = "/api/v1/assistant/actions";
    private const int MaximumRegistryBytes = 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NyxIdToolOptions _options;

    public NyxIdAssistantActionRegistryHttpSource(
        IHttpClientFactory httpClientFactory,
        NyxIdToolOptions options)
    {
        _httpClientFactory = httpClientFactory ??
                             throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<string> FetchAsync(CancellationToken ct)
    {
        var baseUrl = _options.BaseUrl?.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
             !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The NyxID API base URL is invalid for the Assistant action registry.");
        }

        var registryUri = new Uri(
            $"{baseUri.GetLeftPart(UriPartial.Authority)}{baseUri.AbsolutePath.TrimEnd('/')}{RegistryPath}",
            UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, registryUri);
        using var response = await _httpClientFactory
            .CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumRegistryBytes)
        {
            throw new InvalidOperationException(
                "The NyxID Assistant action registry exceeds the maximum size.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var registry = new System.Text.StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            registry.Append(buffer, 0, read);
            if (registry.Length > MaximumRegistryBytes)
            {
                throw new InvalidOperationException(
                    "The NyxID Assistant action registry exceeds the maximum size.");
            }
        }

        return registry.ToString();
    }
}

internal sealed class NyxIdAssistantActionRegistryStartupService : IHostedService
{
    private readonly INyxIdAssistantActionRegistrySource _source;
    private readonly NyxIdAssistantActionRegistrySnapshot _snapshot;

    public NyxIdAssistantActionRegistryStartupService(
        INyxIdAssistantActionRegistrySource source,
        NyxIdAssistantActionRegistrySnapshot snapshot)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var json = await _source.FetchAsync(cancellationToken).ConfigureAwait(false);
        var registry = NyxIdAssistantActionRegistry.Load(json);
        _snapshot.Initialize(registry);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
