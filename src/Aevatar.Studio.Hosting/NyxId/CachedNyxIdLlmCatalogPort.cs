using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Hosting.NyxId;

// Refactor (iter159/cluster-646-first):
//   Old pattern: Every IUserLlmCatalogPort.GetServicesAsync call fetched NyxID directly during the request path,
//                amplifying NyxID catalog IO across hot-path Studio requests.
//   New principle: Host/NyxID adapter owns a bounded stale-while-revalidate snapshot, keyed by NyxID authority +
//                  caller bearer fingerprint. The snapshot is a non-authoritative performance hint — NOT a readmodel,
//                  NOT actor state, NOT a query fact source. Authoritative facts remain in NyxID; cache eviction,
//                  miss, or disabled mode falls back to authoritative fetch. ProvisionAsync invalidates the caller's
//                  snapshot to keep stale window honest.
internal sealed class CachedNyxIdLlmCatalogPort : IUserLlmCatalogPort, IDisposable
{
    private readonly IUserLlmCatalogPort _inner;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<NyxIdLlmCatalogCacheOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CachedNyxIdLlmCatalogPort> _logger;
    private readonly INyxIdCatalogSnapshotCommandPort? _snapshotCommandPort;
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<NyxIdLlmCatalogCacheKey, byte> _refreshingKeys = new();

    public CachedNyxIdLlmCatalogPort(
        IUserLlmCatalogPort inner,
        IConfiguration configuration,
        IOptionsMonitor<NyxIdLlmCatalogCacheOptions> options,
        TimeProvider timeProvider,
        ILogger<CachedNyxIdLlmCatalogPort> logger,
        INyxIdCatalogSnapshotCommandPort? snapshotCommandPort = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _snapshotCommandPort = snapshotCommandPort;

        var maxEntries = NormalizeMaxEntries(_options.CurrentValue.MaxEntries);
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxEntries });
    }

    public async Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var options = NormalizeOptions(_options.CurrentValue);
        if (!options.Enabled)
            return await _inner.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);

        var key = BuildKey(bearerToken);
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(key, out NyxIdLlmCatalogSnapshotEntry? entry) && entry is not null)
        {
            if (now < entry.FreshUntilUtc)
                return entry.Result;

            if (now < entry.StaleUntilUtc)
            {
                TriggerRefresh(key, bearerToken, options);
                return entry.Result;
            }
        }

        var result = await FetchAndObserveAsync(bearerToken, options, ct).ConfigureAwait(false);
        Store(key, result, options);
        return result;
    }

    public async Task<NyxIdLlmService> ProvisionAsync(
        string bearerToken,
        string provisionEndpointId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var service = await _inner.ProvisionAsync(bearerToken, provisionEndpointId, ct)
            .ConfigureAwait(false);

        if (TryResolveOwner(bearerToken, out var owner) && _snapshotCommandPort != null)
            await _snapshotCommandPort.InvalidateAsync(owner, _timeProvider.GetUtcNow(), "catalog_mutated", ct);

        if (NormalizeOptions(_options.CurrentValue).Enabled)
        {
            var key = BuildKey(bearerToken);
            _cache.Remove(key);
            _refreshingKeys.TryRemove(key, out _);
        }

        return service;
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    private void TriggerRefresh(
        NyxIdLlmCatalogCacheKey key,
        string bearerToken,
        NyxIdLlmCatalogCacheOptions options)
    {
        if (!_refreshingKeys.TryAdd(key, 0))
            return;

        _ = RefreshAndClearAsync(key, bearerToken, options);
    }

    private async Task RefreshAndClearAsync(
        NyxIdLlmCatalogCacheKey key,
        string bearerToken,
        NyxIdLlmCatalogCacheOptions options)
    {
        try
        {
            var result = await FetchAndObserveAsync(bearerToken, options, CancellationToken.None).ConfigureAwait(false);
            Store(key, result, options);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "NyxID LLM catalog SWR refresh was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID LLM catalog SWR refresh failed.");
            if (TryResolveOwner(bearerToken, out var owner) && _snapshotCommandPort != null)
            {
                await _snapshotCommandPort.RecordRefreshFailureAsync(
                    owner, _timeProvider.GetUtcNow(), "catalog_refresh_failed", CancellationToken.None);
            }
        }
        finally
        {
            _refreshingKeys.TryRemove(key, out _);
        }
    }

    private async Task<NyxIdLlmServicesResult> FetchAndObserveAsync(
        string bearerToken,
        NyxIdLlmCatalogCacheOptions options,
        CancellationToken ct)
    {
        var result = await _inner.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
        if (!TryResolveOwner(bearerToken, out var owner) || _snapshotCommandPort == null)
            return result;

        var observedAt = _timeProvider.GetUtcNow();
        var services = result.Services.Select(static service => new NyxIdServiceGrant
        {
            UserServiceId = service.UserServiceId,
            ServiceSlug = service.ServiceSlug,
            DisplayName = service.DisplayName,
            NodeGrantsNotRequired = true,
        }).ToArray();
        var canonical = string.Join('\n', services
            .OrderBy(static service => service.UserServiceId, StringComparer.Ordinal)
            .Select(static service => $"{service.UserServiceId}\t{service.ServiceSlug}\t{service.DisplayName}"));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        await _snapshotCommandPort.ObserveAsync(new NyxIdCatalogObservation(
            owner,
            observedAt,
            observedAt + options.FreshTtl,
            digest,
            digest,
            services), ct);
        return result;
    }

    private bool TryResolveOwner(string bearerToken, out NyxIdCatalogOwnerIdentity owner)
    {
        owner = null!;
        var segments = bearerToken.Split('.');
        if (segments.Length < 2)
            return false;
        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!document.RootElement.TryGetProperty("sub", out var subject) ||
                string.IsNullOrWhiteSpace(subject.GetString()))
            {
                return false;
            }

            var authority = NyxIdAuthorityResolver.ResolveNyxIdAuthorityBase(_configuration);
            if (string.IsNullOrWhiteSpace(authority))
                return false;
            owner = new NyxIdCatalogOwnerIdentity
            {
                Authority = authority,
                OwnerKind = NyxIdCatalogOwnerKind.Personal,
                OwnerSubject = subject.GetString()!.Trim(),
            };
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void Store(
        NyxIdLlmCatalogCacheKey key,
        NyxIdLlmServicesResult result,
        NyxIdLlmCatalogCacheOptions options)
    {
        var now = _timeProvider.GetUtcNow();
        var freshUntil = now + options.FreshTtl;
        var staleUntil = freshUntil + options.StaleTtl;
        var entry = new NyxIdLlmCatalogSnapshotEntry(result, freshUntil, staleUntil);

        _cache.Set(
            key,
            entry,
            new MemoryCacheEntryOptions().SetSize(1));
    }

    private NyxIdLlmCatalogCacheKey BuildKey(string bearerToken)
    {
        var authority = NyxIdAuthorityResolver.ResolveNyxIdAuthorityBase(_configuration);
        if (string.IsNullOrWhiteSpace(authority))
            throw new InvalidOperationException("NyxID authority is not configured.");

        return new NyxIdLlmCatalogCacheKey(
            authority,
            ComputeSha256Hex(bearerToken));
    }

    private static string ComputeSha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private static NyxIdLlmCatalogCacheOptions NormalizeOptions(NyxIdLlmCatalogCacheOptions options)
    {
        return new NyxIdLlmCatalogCacheOptions
        {
            Enabled = options.Enabled,
            FreshTtl = options.FreshTtl > TimeSpan.Zero ? options.FreshTtl : TimeSpan.FromSeconds(60),
            StaleTtl = options.StaleTtl >= TimeSpan.Zero ? options.StaleTtl : TimeSpan.Zero,
            MaxEntries = NormalizeMaxEntries(options.MaxEntries),
        };
    }

    private static int NormalizeMaxEntries(int value) => value > 0 ? value : 1024;

    private sealed record NyxIdLlmCatalogSnapshotEntry(
        NyxIdLlmServicesResult Result,
        DateTimeOffset FreshUntilUtc,
        DateTimeOffset StaleUntilUtc);

    private readonly record struct NyxIdLlmCatalogCacheKey(
        string Authority,
        string BearerFingerprint);
}
