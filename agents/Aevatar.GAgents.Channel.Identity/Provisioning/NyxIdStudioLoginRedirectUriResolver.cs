using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Resolves the browser callback URL registered for Studio Console NyxID
/// login. This is intentionally separate from <see cref="NyxIdRedirectUriResolver"/>,
/// which is the backend Lark/channel binding callback that consumes signed
/// state tokens.
/// </summary>
public static class NyxIdStudioLoginRedirectUriResolver
{
    public const string DefaultPublicBaseUrl = "https://dashboard.aevatar.ai";
    public const string CallbackPath = "/auth/callback";
    public const string OverrideEnvVar = "AEVATAR_STUDIO_LOGIN_REDIRECT_BASE_URL";

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
                "Ignoring {EnvVar}='{Value}': it is a Kestrel listen address and not a browser-reachable Studio login callback target. Falling back to '{Default}'.",
                OverrideEnvVar,
                trimmed,
                DefaultPublicBaseUrl);
            return DefaultPublicBaseUrl;
        }

        return trimmed;
    }

    private static bool IsWildcardListenAddress(string url)
    {
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
