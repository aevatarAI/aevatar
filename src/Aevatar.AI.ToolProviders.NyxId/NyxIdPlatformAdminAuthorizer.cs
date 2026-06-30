using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.ToolProviders.NyxId;

// 06-20-observatory-admin-cross-scope (G1/G8): NyxID-backed platform-admin authorizer.
//   Calls NyxID /api/v1/users/me with the caller's own bearer and reads the authoritative platform `role`.
//   Strictly fail-closed: elevated ONLY when HTTP 200 + valid JSON object + no {"error":true} envelope + a
//   role that is exactly "admin" or "operator". Caches only positive decisions, per token, short TTL.
public sealed class NyxIdPlatformAdminAuthorizer : IPlatformAdminAuthorizer
{
    // Per-process random salt: cache keys are non-reversible and never portable across processes. Never logged.
    private static readonly byte[] CacheSalt = RandomNumberGenerator.GetBytes(32);

    private static readonly HashSet<string> ElevatedRoles =
        new(StringComparer.OrdinalIgnoreCase) { "admin", "operator" };

    private readonly INyxIdUserReadApi _userReadApi;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _cacheTtl;
    private readonly bool _crossScopeEnabled;
    private readonly ILogger<NyxIdPlatformAdminAuthorizer> _logger;

    public NyxIdPlatformAdminAuthorizer(
        INyxIdUserReadApi userReadApi,
        IMemoryCache cache,
        IOptions<ObservatoryAdminAuthorizationOptions> options,
        ILogger<NyxIdPlatformAdminAuthorizer>? logger = null)
    {
        _userReadApi = userReadApi;
        _cache = cache;
        var ttlSeconds = options.Value.AdminRoleCacheTtlSeconds;
        _cacheTtl = TimeSpan.FromSeconds(ttlSeconds > 0 ? ttlSeconds : 30);
        _crossScopeEnabled = options.Value.CrossScopeEnabled;
        _logger = logger ?? NullLogger<NyxIdPlatformAdminAuthorizer>.Instance;
    }

    public async Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
    {
        // Kill-switch (G8): when disabled, nobody is elevated and we never call NyxID. Takes effect on restart.
        if (!_crossScopeEnabled || string.IsNullOrWhiteSpace(bearerToken))
            return PlatformCaller.NotElevated;

        var cacheKey = BuildCacheKey(bearerToken);
        if (_cache.TryGetValue(cacheKey, out PlatformCaller? cached) && cached is { IsElevated: true })
            return cached;

        string raw;
        try
        {
            raw = await _userReadApi.GetCurrentUserAsync(bearerToken, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // caller aborted — propagate, never treat as elevated
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID /users/me failed while resolving platform admin status; denying.");
            return PlatformCaller.NotElevated;
        }

        var caller = ParseCaller(raw);

        // G8: cache ONLY a positive (elevated) decision. Never cache a denial/error — a transient NyxID failure
        // must not pin "not admin", and a freshly granted admin must not be stuck denied for the TTL.
        if (caller.IsElevated)
            _cache.Set(cacheKey, caller, _cacheTtl);

        return caller;
    }

    // Strict fail-closed parse (G1). Public-internal for unit tests covering the full matrix.
    internal static PlatformCaller ParseCaller(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return PlatformCaller.NotElevated;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return PlatformCaller.NotElevated;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return PlatformCaller.NotElevated;

            // NyxIdApiClient encodes non-2xx as {"error":true,...} — fail closed.
            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.True)
                return PlatformCaller.NotElevated;

            var role = GetString(root, "role").Trim();
            if (role.Length == 0 || !ElevatedRoles.Contains(role))
                return PlatformCaller.NotElevated;

            return new PlatformCaller(true, role, GetString(root, "email"), GetString(root, "id"));
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;

    private static string BuildCacheKey(string token)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var buffer = new byte[CacheSalt.Length + tokenBytes.Length];
        Buffer.BlockCopy(CacheSalt, 0, buffer, 0, CacheSalt.Length);
        Buffer.BlockCopy(tokenBytes, 0, buffer, CacheSalt.Length, tokenBytes.Length);
        var hash = SHA256.HashData(buffer);
        return "observatory:admin:" + Convert.ToHexString(hash);
    }
}
