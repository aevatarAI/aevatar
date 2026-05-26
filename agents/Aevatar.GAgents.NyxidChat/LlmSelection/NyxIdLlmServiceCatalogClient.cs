using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class NyxIdLlmServiceCatalogClient : INyxIdLlmServiceCatalogClient
{
    private static readonly TimeSpan ProxyServicesCacheTtl = TimeSpan.FromSeconds(30);
    private const string ProxyServicesCacheKeyPrefix = "nyxid-llm-svc:proxy-services:";

    private readonly NyxIdApiClient _nyxClient;
    private readonly IMemoryCache _proxyServicesCache;
    private readonly ILogger<NyxIdLlmServiceCatalogClient> _logger;

    public NyxIdLlmServiceCatalogClient(
        NyxIdApiClient nyxClient,
        IMemoryCache proxyServicesCache,
        ILogger<NyxIdLlmServiceCatalogClient>? logger = null)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _proxyServicesCache = proxyServicesCache ?? throw new ArgumentNullException(nameof(proxyServicesCache));
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

    /// <summary>
    /// Cache the per-user <c>/api/v1/proxy/services</c> response for a short TTL so a flurry
    /// of /model invocations from the same user collapses onto one upstream call. We use
    /// <see cref="IMemoryCache"/> rather than a singleton dictionary so the cache backing
    /// store is shared, sized, and evicted per the host's standard memory-cache policy
    /// (CLAUDE.md §"中间层状态约束" — services don't own per-caller state directly).
    /// </summary>
    private async Task<string> DiscoverProxyServicesCachedAsync(
        string accessToken,
        CancellationToken ct)
    {
        var cacheKey = ProxyServicesCacheKeyPrefix + ComputeTokenFingerprint(accessToken);
        if (_proxyServicesCache.TryGetValue(cacheKey, out string? cached) &&
            !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        var response = await _nyxClient.DiscoverProxyServicesAsync(accessToken, ct).ConfigureAwait(false);
        // Size is not set on the entry — IMemoryCache only enforces Size when the host
        // configured a SizeLimit on MemoryCacheOptions. The cache backing store is owned
        // by the host (we register IMemoryCache via AddMemoryCache, no per-entry size
        // policy from us), so leave eviction to the host's TimeBasedExpiration default.
        _proxyServicesCache.Set(
            cacheKey,
            response,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ProxyServicesCacheTtl,
            });
        return response;
    }

    private static string ComputeTokenFingerprint(string accessToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
}
