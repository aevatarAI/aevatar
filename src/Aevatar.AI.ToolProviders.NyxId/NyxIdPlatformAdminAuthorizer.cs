using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.ToolProviders.NyxId;

// NyxID-backed current-user resolver with aevatar-owned admin policy.
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
    private readonly HashSet<string> _allowedUserIds;
    private readonly HashSet<string> _allowedEmails;
    private readonly bool _trustNyxIdPlatformRole;
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
        _allowedUserIds = NormalizeUserIds(options.Value.AllowedUserIds);
        _allowedEmails = NormalizeEmails(options.Value.AllowedEmails);
        _trustNyxIdPlatformRole = options.Value.TrustNyxIdPlatformRole;
        _logger = logger ?? NullLogger<NyxIdPlatformAdminAuthorizer>.Instance;
    }

    public async Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
    {
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
            _logger.LogWarning(ex, "NyxID /users/me failed while resolving current user for admin access; denying.");
            return PlatformCaller.NotElevated;
        }

        var identity = ParseCurrentUser(raw);
        var caller = Authorize(identity);

        if (caller.IsElevated)
            _cache.Set(cacheKey, caller, _cacheTtl);

        return caller;
    }

    internal static NyxIdCurrentUser ParseCurrentUser(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return NyxIdCurrentUser.Invalid;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return NyxIdCurrentUser.Invalid;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return NyxIdCurrentUser.Invalid;

            // NyxIdApiClient encodes non-2xx as {"error":true,...} — fail closed.
            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.True)
                return NyxIdCurrentUser.Invalid;

            var role = GetString(root, "role").Trim();
            var email = NormalizeEmail(GetString(root, "email"));
            var userId = GetString(root, "id").Trim();
            if (string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(email))
                return NyxIdCurrentUser.Invalid;

            return new NyxIdCurrentUser(true, role, email, userId);
        }
    }

    private PlatformCaller Authorize(NyxIdCurrentUser identity)
    {
        if (!identity.IsValid)
            return PlatformCaller.NotElevated;

        if (!string.IsNullOrWhiteSpace(identity.UserId) && _allowedUserIds.Contains(identity.UserId))
        {
            return new PlatformCaller(
                true,
                identity.Role,
                identity.Email,
                identity.UserId,
                PlatformAdminGrantSources.AllowedUserId);
        }

        if (!string.IsNullOrWhiteSpace(identity.Email) && _allowedEmails.Contains(identity.Email))
        {
            return new PlatformCaller(
                true,
                identity.Role,
                identity.Email,
                identity.UserId,
                PlatformAdminGrantSources.AllowedEmail);
        }

        if (_trustNyxIdPlatformRole && ElevatedRoles.Contains(identity.Role))
        {
            return new PlatformCaller(
                true,
                identity.Role,
                identity.Email,
                identity.UserId,
                PlatformAdminGrantSources.NyxIdPlatformRole);
        }

        return PlatformCaller.NotElevated;
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;

    private static HashSet<string> NormalizeUserIds(IEnumerable<string>? values) =>
        values?
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal) ??
        new HashSet<string>(StringComparer.Ordinal);

    private static HashSet<string> NormalizeEmails(IEnumerable<string>? values) =>
        values?
            .Select(NormalizeEmail)
            .Where(static value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal) ??
        new HashSet<string>(StringComparer.Ordinal);

    private static string NormalizeEmail(string value) => value.Trim().ToLowerInvariant();

    private static string BuildCacheKey(string token)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var buffer = new byte[CacheSalt.Length + tokenBytes.Length];
        Buffer.BlockCopy(CacheSalt, 0, buffer, 0, CacheSalt.Length);
        Buffer.BlockCopy(tokenBytes, 0, buffer, CacheSalt.Length, tokenBytes.Length);
        var hash = SHA256.HashData(buffer);
        return "aevatar:admin-access:" + Convert.ToHexString(hash);
    }

    internal sealed record NyxIdCurrentUser(bool IsValid, string Role, string Email, string UserId)
    {
        public static NyxIdCurrentUser Invalid { get; } = new(false, string.Empty, string.Empty, string.Empty);
    }
}
