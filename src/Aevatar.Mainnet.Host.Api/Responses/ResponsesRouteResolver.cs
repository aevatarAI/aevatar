using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Responses;

/// <summary>Resolves an OpenRouter-style vendor prefix (e.g. `anthropic`) into the full NyxID
/// route path (e.g. `/api/v1/llm/anthropic/v1`) by consulting the user's LLM service catalog.
/// Used by `/v1/responses` to convert a `vendor/model` request into a `NyxIdRoutePreference`
/// that <see cref="Aevatar.AI.LLMProviders.NyxId.NyxIdLLMProvider"/> can route. Returns
/// <c>null</c> when the slug isn't a known service — caller falls back to default gateway
/// routing (treats the whole string as a bare model name).</summary>
internal interface IResponsesRouteResolver
{
    Task<string?> ResolveRouteValueAsync(
        string slug,
        string bearerToken,
        CancellationToken ct);
}

internal sealed class CachingResponsesRouteResolver : IResponsesRouteResolver
{
    /// <summary>Catalog HTTP fetches are expensive (2 NyxID round-trips). Cache per caller
    /// (bearer-keyed because the catalog is per-user-filtered) with a short TTL so revocations
    /// and new service bindings surface within a few minutes. Bearer is hashed before keying
    /// to keep raw tokens out of in-memory state.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IUserLlmCatalogPort _catalog;
    private readonly ILogger<CachingResponsesRouteResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public CachingResponsesRouteResolver(
        IUserLlmCatalogPort catalog,
        ILogger<CachingResponsesRouteResolver> logger,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string?> ResolveRouteValueAsync(
        string slug,
        string bearerToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var normalizedSlug = slug.Trim();
        var routes = await GetRoutesAsync(bearerToken, ct).ConfigureAwait(false);
        return routes.TryGetValue(normalizedSlug, out var routeValue) ? routeValue : null;
    }

    private async Task<IReadOnlyDictionary<string, string>> GetRoutesAsync(
        string bearerToken,
        CancellationToken ct)
    {
        var key = HashBearer(bearerToken);
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            return cached.Routes;

        NyxIdLlmServicesResult result;
        try
        {
            result = await _catalog.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog fetch failed; route resolution will treat all slugs as unknown.");
            return EmptyRoutes;
        }

        var routes = BuildRouteMap(result);
        _cache[key] = new CacheEntry(routes, now + CacheTtl);
        return routes;
    }

    private static Dictionary<string, string> BuildRouteMap(NyxIdLlmServicesResult result)
    {
        var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in result.Services)
        {
            // Note: we do NOT gate on `service.Allowed`. Catalog `Allowed=false` flags
            // "user hasn't bound a credential" (a UI hint), not "this route can't serve
            // requests" — several services with `requires_connection=true` are still
            // routable for the caller's bearer if the backing LLM is shared. Map every
            // service with a slug+route; downstream HTTP error tells the truth if the
            // caller actually can't reach the upstream.
            if (string.IsNullOrWhiteSpace(service.ServiceSlug)) continue;
            if (string.IsNullOrWhiteSpace(service.RouteValue)) continue;
            // First-write-wins. Different service shapes (GatewayProvider via /llm/<slug>/v1
            // vs ProxyService via /proxy/s/<slug>) should never share a slug — but if they
            // somehow do, prefer the entry the catalog returned first.
            routes.TryAdd(service.ServiceSlug.Trim(), service.RouteValue.Trim());
        }
        return routes;
    }

    private static string HashBearer(string bearerToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(bearerToken));
        return Convert.ToHexString(bytes);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyRoutes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly record struct CacheEntry(
        IReadOnlyDictionary<string, string> Routes,
        DateTimeOffset ExpiresAt);
}
