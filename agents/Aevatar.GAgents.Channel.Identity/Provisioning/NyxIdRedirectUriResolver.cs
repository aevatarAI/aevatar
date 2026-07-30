using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Resolves the OAuth callback URL registered on the configured NyxID client
/// and sent at authorize / token-exchange time. Both uses MUST resolve to the
/// same PUBLIC URL, so it has to be a real hostname the browser can reach.
/// </summary>
/// <remarks>
/// Mirrors <see cref="NyxIdAuthorityResolver"/>: hardcoded production
/// default + env-var override for staging / dev. Production deploys are
/// zero-config — they get the right callback URL automatically. The
/// resolver deliberately does NOT read <c>ASPNETCORE_URLS</c> /
/// <c>IConfiguration[ServerUrls]</c> because Kestrel listen addresses
/// (typically <c>http://+:8080</c> in K8s) are not valid OAuth callback
/// targets. The aismart-app-mainnet 2026-04-30 incident — where a wildcard
/// listen address propagated into the registered redirect_uri and every
/// /init's authorize URL was unreachable — was the original motivation
/// for ripping that priority chain out.
/// </remarks>
public static class NyxIdRedirectUriResolver
{
    /// <summary>
    /// Production aevatar console backend origin. Hardcoded so cluster
    /// startup has zero config dependency: prod gets the right callback
    /// URL automatically. Override via <see cref="OverrideEnvVar"/> for
    /// staging / dev / test deploys.
    /// </summary>
    public const string DefaultPublicBaseUrl = "https://aevatar-console-backend-api.aevatar.ai";

    /// <summary>
    /// Path the OAuth callback endpoint is mapped under (see
    /// <c>IdentityOAuthEndpoints.MapIdentityOAuthEndpoints</c>).
    /// </summary>
    public const string CallbackPath = "/api/oauth/nyxid-callback";

    /// <summary>
    /// Optional env-var override for non-production clusters. Production
    /// deploys do NOT set this; they rely on
    /// <see cref="DefaultPublicBaseUrl"/>. Staging / dev clusters that
    /// run on a different hostname set this to their own origin.
    /// </summary>
    public const string OverrideEnvVar = "AEVATAR_OAUTH_REDIRECT_BASE_URL";

    /// <summary>
    /// Optional comma/semicolon/newline separated list of additional redirect
    /// URIs to register on the same NyxID OAuth client. Used for external
    /// callbacks such as the Console SPA <c>/auth/callback</c> route. The
    /// backend does not infer these deployment-specific frontend URLs.
    /// </summary>
    public const string AdditionalRedirectUrisEnvVar = "AEVATAR_OAUTH_ADDITIONAL_REDIRECT_URIS";

    /// <summary>
    /// Returns the absolute callback URL client registration + authorize must use. Reads
    /// <see cref="OverrideEnvVar"/> if set; otherwise returns the
    /// hardcoded production default. A wildcard / unspecified-host
    /// override (e.g. <c>http://+:8080</c>) is rejected with a warning
    /// so a misconfigured non-prod cluster does not silently register a
    /// non-functional redirect URI.
    /// </summary>
    public static string Resolve(ILogger? logger = null)
    {
        var baseUrl = ResolveBaseUrl(logger);
        return $"{baseUrl.TrimEnd('/')}{CallbackPath}";
    }

    public static IReadOnlyList<string> ResolveRegisteredRedirectUris(ILogger? logger = null)
    {
        var values = new List<string> { Resolve(logger) };
        var additional = Environment.GetEnvironmentVariable(AdditionalRedirectUrisEnvVar);
        if (!string.IsNullOrWhiteSpace(additional))
        {
            foreach (var candidate in additional.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsWildcardListenAddress(candidate))
                {
                    logger?.LogWarning(
                        "Ignoring additional OAuth redirect URI from {EnvVar}: '{Value}' is a wildcard / unspecified-host listen address.",
                        AdditionalRedirectUrisEnvVar,
                        candidate);
                    continue;
                }

                values.Add(candidate);
            }
        }

        return NormalizeRedirectUris(values);
    }

    public static IReadOnlyList<string> NormalizeRedirectUris(IEnumerable<string> redirectUris)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<string>();
        foreach (var raw in redirectUris)
        {
            var normalized = raw?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (unique.Add(normalized))
                values.Add(normalized);
        }

        return values;
    }

    private static string ResolveBaseUrl(ILogger? logger)
    {
        var raw = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultPublicBaseUrl;

        var trimmed = raw.Trim();
        if (!TryResolveExplicitBaseUrl(trimmed, out var baseUrl))
        {
            logger?.LogWarning(
                "Ignoring {EnvVar}='{Value}': it is a Kestrel listen address (wildcard / unspecified host) " +
                "and not a valid OAuth callback target. Falling back to the production default '{Default}'. " +
                "Set {EnvVar} to a publicly-reachable origin (e.g. https://staging.example.com) for non-prod clusters.",
                OverrideEnvVar,
                trimmed,
                DefaultPublicBaseUrl,
                OverrideEnvVar);
            return DefaultPublicBaseUrl;
        }

        return baseUrl;
    }

    internal static bool TryResolveExplicitBaseUrl(string? raw, out string baseUrl)
    {
        baseUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (IsWildcardListenAddress(trimmed))
            return false;

        baseUrl = trimmed;
        return true;
    }

    /// <summary>
    /// Detects Kestrel listen-address shapes that cannot serve as an OAuth
    /// redirect URI: <c>+</c>, <c>*</c>, <c>0.0.0.0</c>, IPv6 unspecified
    /// <c>[::]</c>. Match is intentionally narrow — anything with a real
    /// hostname (incl. loopback) is accepted.
    /// </summary>
    private static bool IsWildcardListenAddress(string url)
    {
        // Uri.TryCreate accepts "http://+:8080" and parses host as "+";
        // be defensive against parser tightening in future runtimes.
        if (url.Contains("://+", StringComparison.Ordinal)
            || url.Contains("://*", StringComparison.Ordinal))
        {
            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;

        var host = parsed.Host;
        return host is "+" or "*" or "0.0.0.0"
            || string.Equals(host, "[::]", StringComparison.Ordinal)
            || string.Equals(host, "::", StringComparison.Ordinal);
    }
}
