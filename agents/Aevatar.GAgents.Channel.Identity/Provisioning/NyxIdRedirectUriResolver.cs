using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Resolves the OAuth callback URL the broker registers at NyxID DCR and
/// the URL it sends to NyxID at authorize / token-exchange time. Both call
/// sites MUST resolve to the same URL — if DCR registers
/// <c>http://127.0.0.1:5080/api/oauth/nyxid-callback</c> but the authorize
/// flow uses the public hostname, NyxID rejects the token exchange with
/// <c>invalid_redirect_uri</c>. (PR #521 Codex P1.)
///
/// Source priority — chosen so the same value is observable from any DI
/// scope, including hosted services that don't have an HTTP request:
///   1. <see cref="IConfiguration"/> key <see cref="WebHostDefaults.ServerUrlsKey"/>
///      (when one is supplied) — what the host actually binds to.
///   2. <c>ASPNETCORE_URLS</c> environment variable.
///   3. <c>AEVATAR_SERVER_URLS</c> environment variable — the legacy alias
///      production deploys still set.
///   4. Loopback fallback (<c>http://127.0.0.1:5080</c>).
/// </summary>
public static class NyxIdRedirectUriResolver
{
    /// <summary>
    /// Path the OAuth callback endpoint is mapped under (see
    /// <c>IdentityOAuthEndpoints.MapIdentityOAuthEndpoints</c>).
    /// </summary>
    public const string CallbackPath = "/api/oauth/nyxid-callback";

    /// <summary>
    /// Loopback default used when no host URL is configured (typical for
    /// local dev / unit tests).
    /// </summary>
    public const string LoopbackBaseUrl = "http://127.0.0.1:5080";

    /// <summary>
    /// Resolve the absolute callback URL. <paramref name="configuration"/>
    /// may be null when the caller doesn't have access to the host config
    /// (e.g. broker constructor). The env-var fallbacks pick up the same
    /// hostname production sets so DCR + authorize agree.
    /// </summary>
    public static string Resolve(IConfiguration? configuration = null)
    {
        var firstUrl = ResolveServerBaseUrl(configuration);
        return $"{firstUrl.TrimEnd('/')}{CallbackPath}";
    }

    private static string ResolveServerBaseUrl(IConfiguration? configuration)
    {
        var configured = configuration?[WebHostDefaults.ServerUrlsKey];
        if (TryFirstUrl(configured, out var fromConfig))
            return fromConfig;

        if (TryFirstUrl(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"), out var fromAspNet))
            return fromAspNet;

        if (TryFirstUrl(Environment.GetEnvironmentVariable("AEVATAR_SERVER_URLS"), out var fromAevatar))
            return fromAevatar;

        return LoopbackBaseUrl;
    }

    private static bool TryFirstUrl(string? raw, out string url)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            url = string.Empty;
            return false;
        }

        var first = raw.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(first))
        {
            url = string.Empty;
            return false;
        }

        url = first;
        return true;
    }
}
