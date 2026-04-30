using Microsoft.Extensions.Logging;

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
    /// callers can concatenate paths uniformly. When the env var is unset
    /// AND <c>ASPNETCORE_ENVIRONMENT</c> indicates a non-Development
    /// environment, logs a warning so a staging / dev cluster that forgot
    /// to override does not silently register clients against production
    /// NyxID (PR #521 review mimo-v2.5-pro).
    /// </summary>
    public static string Resolve(ILogger? logger = null)
    {
        var raw = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? string.Empty;
            // "Development" is the .NET stock convention; treat anything
            // that is empty or starts with "dev" as developer-machine intent
            // and skip the warning. Production / Staging / QA see the warning.
            var looksLikeDev = string.IsNullOrEmpty(environmentName)
                || environmentName.StartsWith("dev", StringComparison.OrdinalIgnoreCase)
                || environmentName.StartsWith("local", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeDev)
            {
                logger?.LogWarning(
                    "NyxID authority falling back to hardcoded production default '{Default}' " +
                    "because {EnvVar} is unset; environment={Environment}. Staging / dev clusters " +
                    "MUST set {EnvVar} or they will register OAuth clients against the production NyxID.",
                    DefaultAuthority,
                    OverrideEnvVar,
                    environmentName,
                    OverrideEnvVar);
            }
            return DefaultAuthority.TrimEnd('/');
        }

        return raw.Trim().TrimEnd('/');
    }
}
