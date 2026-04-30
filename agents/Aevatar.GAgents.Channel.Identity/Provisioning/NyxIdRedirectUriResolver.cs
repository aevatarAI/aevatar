using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Resolves the OAuth callback URL the broker registers at NyxID DCR and
/// the URL it sends to NyxID at authorize / token-exchange time. Both call
/// sites MUST resolve to the same PUBLIC URL — DCR's redirect_uri is
/// echoed back to the user's browser at /authorize, so it has to be a real
/// hostname the browser can reach.
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
    /// Returns the absolute callback URL DCR + authorize must use. Reads
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

    private static string ResolveBaseUrl(ILogger? logger)
    {
        var raw = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultPublicBaseUrl;

        var trimmed = raw.Trim();
        if (IsWildcardListenAddress(trimmed))
        {
            logger?.LogWarning(
                "Ignoring {EnvVar}='{Value}': it is a Kestrel listen address (wildcard / unspecified host) " +
                "and not a valid OAuth callback target. Falling back to the production default '{Default}'. " +
                "Set {EnvVar} to a publicly-reachable origin (e.g. https://staging.example.com) for non-prod clusters.",
                OverrideEnvVar,
                trimmed,
                DefaultPublicBaseUrl);
            return DefaultPublicBaseUrl;
        }

        return trimmed;
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
