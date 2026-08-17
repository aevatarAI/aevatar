using Microsoft.Extensions.Configuration;

namespace Aevatar.Configuration;

/// <summary>Resolves NyxID endpoint roles without exposing cluster-local transport addresses.</summary>
public static class NyxIdEndpointResolver
{
    private const string GatewayPath = "/api/v1/llm/gateway/v1";
    private const string InternalApiBaseUrlKey = "Aevatar:NyxId:InternalApiBaseUrl";
    private const string ApiBaseUrlKey = "Aevatar:NyxId:ApiBaseUrl";
    private const string CliAuthorityKey = "Cli:App:NyxId:Authority";
    private const string AppAuthorityKey = "Aevatar:NyxId:Authority";
    private const string AuthAuthorityKey = "Aevatar:Authentication:Authority";

    /// <summary>
    /// Returns the browser-reachable NyxID REST base URL. Authority aliases are accepted only
    /// for legacy single-endpoint deployments that do not configure an internal transport.
    /// </summary>
    public static string? ResolvePublicApiBaseUrl(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredApiBaseUrl = FirstConfiguredValue(configuration, ApiBaseUrlKey);
        if (configuredApiBaseUrl is not null)
            return NormalizePublicBaseUrl(configuredApiBaseUrl);

        if (FirstConfiguredValue(configuration, InternalApiBaseUrlKey) is not null)
            return null;

        return NormalizePublicBaseUrl(FirstConfiguredValue(
            configuration,
            CliAuthorityKey,
            AppAuthorityKey,
            AuthAuthorityKey));
    }

    private static string? NormalizePublicBaseUrl(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed) ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var absolute = uri.ToString().TrimEnd('/');
        return absolute.EndsWith(GatewayPath, StringComparison.OrdinalIgnoreCase)
            ? absolute[..^GatewayPath.Length]
            : absolute;
    }

    private static string? FirstConfiguredValue(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}
