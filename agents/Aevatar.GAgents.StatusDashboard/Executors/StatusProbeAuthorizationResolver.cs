using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Aevatar.GAgents.StatusDashboard.Executors;

public sealed class StatusProbeAuthorizationResolver
{
    private const string ConfigPlaceholderPrefix = "${configuration:";
    private static readonly Regex ConfigPlaceholderRegex = new(
        @"\$\{configuration:(?<key>[^\}]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IProbeServiceTokenProvider? _scopeTokenProvider;
    private readonly ConcurrentDictionary<string, TokenCacheEntry> _tokenCache = new(StringComparer.Ordinal);

    public StatusProbeAuthorizationResolver(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IEnumerable<IProbeServiceTokenProvider>? scopeTokenProviders = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        // Optional, host-supplied. Absent (empty) when scope service tokens are not enabled.
        _scopeTokenProvider = scopeTokenProviders?.FirstOrDefault();
    }

    public async Task<ResolvedBearerToken?> ResolveBearerTokenAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var mode = NormalizeMode(ReadParam(descriptor, "Auth.Mode"));
        return mode switch
        {
            "none" => null,
            "static_bearer" => ResolveStaticBearerToken(descriptor, requireConfigured: true),
            "client_credentials" => await ResolveClientCredentialsTokenAsync(descriptor, requireConfigured: true, ct),
            "scope_service_token" => await ResolveScopeServiceTokenAsync(descriptor, ct),
            "auto" => await ResolveAutoTokenAsync(descriptor, ct),
            _ => throw new InvalidOperationException(
                $"Unsupported Auth.Mode '{mode}'. Expected one of: auto, none, static_bearer, client_credentials, scope_service_token."),
        };
    }

    // Self-issued scope service token (Bearer) for the host's own authenticated endpoints. When the
    // provider is absent (scope service tokens disabled) or cannot mint, throws
    // ProbeCredentialUnavailableException so the executor degrades the probe to "unknown" — never an
    // unauthenticated request that would 401 and read as a false "down".
    private async Task<ResolvedBearerToken?> ResolveScopeServiceTokenAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        var scopeId = ReadParam(descriptor, "Auth.ScopeId")?.Trim();
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ProbeCredentialUnavailableException("Auth.ScopeId is required for scope_service_token auth.");
        if (_scopeTokenProvider is null)
        {
            throw new ProbeCredentialUnavailableException(
                "Scope service token provider is not registered (Aevatar:Authentication:ScopeServiceTokens disabled).");
        }

        var token = await _scopeTokenProvider.GetScopeBearerAsync(scopeId, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new ProbeCredentialUnavailableException("Scope service token provider returned no token.");

        return new ResolvedBearerToken("Bearer", token);
    }

    public async Task<string?> ResolveAuthorizationHeaderAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        var token = await ResolveBearerTokenAsync(descriptor, ct);
        return token is null ? null : $"{token.TokenType} {token.AccessToken}";
    }

    private async Task<ResolvedBearerToken?> ResolveAutoTokenAsync(
        HealthProbeTargetDescriptor descriptor,
        CancellationToken ct)
    {
        if (HasCompleteClientCredentialsConfiguration(descriptor))
            return await ResolveClientCredentialsTokenAsync(descriptor, requireConfigured: true, ct);

        return ResolveStaticBearerToken(descriptor, requireConfigured: false);
    }

    private ResolvedBearerToken? ResolveStaticBearerToken(
        HealthProbeTargetDescriptor descriptor,
        bool requireConfigured)
    {
        var key = FirstNonEmpty(
            ReadParam(descriptor, "Auth.StaticBearerConfigurationKey"),
            ReadParam(descriptor, "BearerTokenConfigurationKey"));
        if (string.IsNullOrWhiteSpace(key))
        {
            if (requireConfigured)
                throw new InvalidOperationException("Auth.StaticBearerConfigurationKey is required for static_bearer auth.");
            return null;
        }

        var token = _configuration[key.Trim()]?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            if (requireConfigured)
                throw new InvalidOperationException("Configured static bearer token is empty.");
            return null;
        }

        return new ResolvedBearerToken("Bearer", token);
    }

    private async Task<ResolvedBearerToken?> ResolveClientCredentialsTokenAsync(
        HealthProbeTargetDescriptor descriptor,
        bool requireConfigured,
        CancellationToken ct)
    {
        var tokenEndpoint = ResolvePlaceholders(ReadParam(descriptor, "Auth.TokenEndpoint") ?? string.Empty);
        var clientIdKey = ReadParam(descriptor, "Auth.ClientIdConfigurationKey");
        var clientSecretKey = ReadParam(descriptor, "Auth.ClientSecretConfigurationKey");
        if (string.IsNullOrWhiteSpace(clientIdKey) ||
            string.IsNullOrWhiteSpace(clientSecretKey) ||
            string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            if (requireConfigured)
            {
                throw new InvalidOperationException(
                    "Auth.TokenEndpoint, Auth.ClientIdConfigurationKey, and Auth.ClientSecretConfigurationKey are required for client_credentials auth.");
            }

            return null;
        }

        var clientId = _configuration[clientIdKey.Trim()];
        var clientSecret = _configuration[clientSecretKey.Trim()];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            if (requireConfigured)
                throw new InvalidOperationException("Configured client_credentials client_id or client_secret is empty.");
            return null;
        }

        if (!Uri.TryCreate(tokenEndpoint.Trim(), UriKind.Absolute, out var uri))
        {
            if (requireConfigured)
                throw new InvalidOperationException("Auth.TokenEndpoint must be an absolute URL.");
            return null;
        }

        var scope = ReadParam(descriptor, "Auth.ClientCredentialsScope")?.Trim();
        var cacheKey = $"{uri}|client_credentials|{clientIdKey.Trim()}|{clientSecretKey.Trim()}|{scope}";
        if (_tokenCache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(RefreshSkew))
        {
            return new ResolvedBearerToken(cached.TokenType, cached.AccessToken);
        }

        var refreshed = await RequestClientCredentialsTokenAsync(
            uri,
            clientId.Trim(),
            clientSecret.Trim(),
            scope,
            ct);
        _tokenCache[cacheKey] = refreshed;
        return new ResolvedBearerToken(refreshed.TokenType, refreshed.AccessToken);
    }

    private async Task<TokenCacheEntry> RequestClientCredentialsTokenAsync(
        Uri tokenEndpoint,
        string clientId,
        string clientSecret,
        string? scope,
        CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        };
        if (!string.IsNullOrWhiteSpace(scope))
            form["scope"] = scope;

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.ParseAdd("application/json");

        var client = _httpClientFactory.CreateClient(nameof(HttpStatusProbeExecutor));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OAuth client_credentials failed with HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("access_token", out var accessTokenElement) ||
            accessTokenElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accessTokenElement.GetString()))
        {
            throw new InvalidOperationException("OAuth client_credentials response did not contain access_token.");
        }

        var tokenType = root.TryGetProperty("token_type", out var tokenTypeElement) &&
                        tokenTypeElement.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(tokenTypeElement.GetString())
            ? tokenTypeElement.GetString()!.Trim()
            : "Bearer";
        var expiresIn = root.TryGetProperty("expires_in", out var expiresInElement)
            ? ReadExpiresInSeconds(expiresInElement)
            : 3600;

        return new TokenCacheEntry(
            accessTokenElement.GetString()!.Trim(),
            tokenType,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresIn)));
    }

    private static int ReadExpiresInSeconds(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) && number > 0)
            return number;
        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), out var textNumber) &&
            textNumber > 0)
        {
            return textNumber;
        }

        return 3600;
    }

    private bool HasCompleteClientCredentialsConfiguration(HealthProbeTargetDescriptor descriptor)
    {
        var tokenEndpoint = ReadParam(descriptor, "Auth.TokenEndpoint");
        var clientIdKey = ReadParam(descriptor, "Auth.ClientIdConfigurationKey");
        var clientSecretKey = ReadParam(descriptor, "Auth.ClientSecretConfigurationKey");
        return !string.IsNullOrWhiteSpace(tokenEndpoint) &&
               !string.IsNullOrWhiteSpace(clientIdKey) &&
               !string.IsNullOrWhiteSpace(clientSecretKey) &&
               !string.IsNullOrWhiteSpace(_configuration[clientIdKey.Trim()]) &&
               !string.IsNullOrWhiteSpace(_configuration[clientSecretKey.Trim()]);
    }

    private static string NormalizeMode(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "auto"
            : normalized.Replace('-', '_').ToLowerInvariant();
    }

    private static string? ReadParam(HealthProbeTargetDescriptor descriptor, string key)
    {
        foreach (var (k, v) in descriptor.Parameters)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private string ResolvePlaceholders(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.Contains(ConfigPlaceholderPrefix, StringComparison.Ordinal))
            return raw;
        return ConfigPlaceholderRegex.Replace(raw, match =>
        {
            var key = match.Groups["key"].Value;
            return _configuration[key] ?? string.Empty;
        });
    }

    private sealed record TokenCacheEntry(
        string AccessToken,
        string TokenType,
        DateTimeOffset ExpiresAtUtc);
}

public sealed record ResolvedBearerToken(string TokenType, string AccessToken);
