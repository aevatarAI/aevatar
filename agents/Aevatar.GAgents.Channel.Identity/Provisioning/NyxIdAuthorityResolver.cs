namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Resolves the NyxID base URL with a hardcoded production default and an
/// env-var override (<c>AEVATAR_NYXID_AUTHORITY</c>). Deliberately does NOT
/// read appsettings — production deployments do not configure
/// <c>Aevatar:NyxId:Authority</c>, and the broker module must work without
/// new appsettings sections.
/// </summary>
public static class NyxIdAuthorityResolver
{
    /// <summary>
    /// Production NyxID base URL. Hardcoded so cluster startup has zero
    /// hidden config dependencies. Override via the environment variable
    /// <c>AEVATAR_NYXID_AUTHORITY</c> for staging / dev / test deploys.
    /// </summary>
    public const string DefaultAuthority = "https://nyx.chrono-ai.fun";

    public const string OverrideEnvVar = "AEVATAR_NYXID_AUTHORITY";

    /// <summary>
    /// Returns the runtime NyxID authority. Trims trailing slash so
    /// callers can concatenate paths uniformly.
    /// </summary>
    public static string Resolve()
    {
        var raw = Environment.GetEnvironmentVariable(OverrideEnvVar);
        var authority = string.IsNullOrWhiteSpace(raw) ? DefaultAuthority : raw.Trim();
        return authority.TrimEnd('/');
    }
}
