using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class NyxIdLlmServiceCatalogClient : INyxIdLlmServiceCatalogClient
{
    private static readonly TimeSpan ProxyServicesCacheTtl = TimeSpan.FromSeconds(30);
    private const int MaxProxyServicesCacheEntries = 128;

    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<NyxIdLlmServiceCatalogClient> _logger;
    private readonly object _proxyServicesCacheLock = new();
    private readonly Dictionary<string, ProxyServicesCacheEntry> _proxyServicesCache = new(StringComparer.Ordinal);

    private sealed record ProxyServicesCacheEntry(string Response, DateTimeOffset ExpiresAtUtc);

    public NyxIdLlmServiceCatalogClient(
        NyxIdApiClient nyxClient,
        ILogger<NyxIdLlmServiceCatalogClient>? logger = null)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? NullLogger<NyxIdLlmServiceCatalogClient>.Instance;
    }

    public async Task<NyxIdLlmServicesResult> GetServicesAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var response = await _nyxClient.GetLlmServicesAsync(accessToken, ct).ConfigureAwait(false);
        var result = NyxIdLlmServiceCatalogParser.ParseServicesResult(response);
        return await MergeProxyRouteCandidatesAsync(result, accessToken, ct).ConfigureAwait(false);
    }

    public async Task<UserLlmSetupHint> GetSetupHintAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct)
    {
        var result = await GetServicesAsync(query, accessToken, ct).ConfigureAwait(false);
        return result.SetupHint ?? new UserLlmSetupHint(string.Empty, []);
    }

    public async Task<NyxIdLlmService> ProvisionAsync(
        UserLlmSelectionContext context,
        string accessToken,
        string provisionEndpointId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionEndpointId);

        var response = await _nyxClient
            .ProvisionLlmServiceAsync(accessToken, provisionEndpointId, ct)
            .ConfigureAwait(false);
        return NyxIdLlmServiceCatalogParser.ParseProvisionedService(response);
    }

    private async Task<NyxIdLlmServicesResult> MergeProxyRouteCandidatesAsync(
        NyxIdLlmServicesResult result,
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            var proxyServices = await DiscoverProxyServicesCachedAsync(accessToken, ct).ConfigureAwait(false);
            return NyxIdLlmServiceCatalogParser.MergeProxyRouteCandidates(result, proxyServices);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to merge NyxID proxy services into LLM route catalog");
            return result;
        }
    }

    private async Task<string> DiscoverProxyServicesCachedAsync(
        string accessToken,
        CancellationToken ct)
    {
        var cacheKey = ComputeTokenFingerprint(accessToken);
        var now = DateTimeOffset.UtcNow;
        lock (_proxyServicesCacheLock)
        {
            if (_proxyServicesCache.TryGetValue(cacheKey, out var cached) &&
                cached.ExpiresAtUtc > now)
            {
                return cached.Response;
            }
        }

        var response = await _nyxClient.DiscoverProxyServicesAsync(accessToken, ct).ConfigureAwait(false);
        var expiresAt = DateTimeOffset.UtcNow.Add(ProxyServicesCacheTtl);
        lock (_proxyServicesCacheLock)
        {
            PruneProxyServicesCache(DateTimeOffset.UtcNow);
            _proxyServicesCache[cacheKey] = new ProxyServicesCacheEntry(response, expiresAt);
        }

        return response;
    }

    private void PruneProxyServicesCache(DateTimeOffset now)
    {
        if (_proxyServicesCache.Count == 0)
            return;

        foreach (var key in _proxyServicesCache
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _proxyServicesCache.Remove(key);
        }

        if (_proxyServicesCache.Count <= MaxProxyServicesCacheEntries)
            return;

        foreach (var key in _proxyServicesCache
                     .OrderBy(pair => pair.Value.ExpiresAtUtc)
                     .Take(_proxyServicesCache.Count - MaxProxyServicesCacheEntries)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _proxyServicesCache.Remove(key);
        }
    }

    private static string ComputeTokenFingerprint(string accessToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
}
