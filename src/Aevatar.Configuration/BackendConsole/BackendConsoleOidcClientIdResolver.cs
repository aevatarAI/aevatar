using Microsoft.Extensions.Configuration;

namespace Aevatar.Configuration.BackendConsole;

/// <summary>
/// Resolves the canonical public OAuth client id shared by every console and
/// broker login surface.
/// </summary>
public static class BackendConsoleOidcClientIdResolver
{
    public const string ConfigurationKey =
        $"{BackendConsoleOptions.SectionName}:{nameof(BackendConsoleOptions.OidcClientId)}";

    public const string EnvironmentVariableName = "HOST_BACKEND_CONSOLE_OIDC_CLIENT_ID";

    public static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var environmentOverride = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        return string.IsNullOrWhiteSpace(environmentOverride)
            ? configuration[ConfigurationKey]?.Trim() ?? string.Empty
            : environmentOverride.Trim();
    }
}
