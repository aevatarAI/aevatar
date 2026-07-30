using Aevatar.Configuration.BackendConsole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aevatar.BackendConsole.Hosting;

public static class BackendConsoleHostingServiceCollectionExtensions
{
    public static IServiceCollection AddBackendConsoleStaticAssets(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IOptions<BackendConsoleOptions>>(_ => Options.Create(BuildOptions(configuration)));
        services.TryAddSingleton<IBackendConsoleAssetService, BackendConsoleAssetService>();
        return services;
    }

    internal static BackendConsoleOptions BuildOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(BackendConsoleOptions.SectionName);
        var options = new BackendConsoleOptions
        {
            OidcAuthority = section[nameof(BackendConsoleOptions.OidcAuthority)] ?? string.Empty,
            OidcClientId = BackendConsoleOidcClientIdResolver.Resolve(configuration),
            OidcScope = section[nameof(BackendConsoleOptions.OidcScope)] ?? string.Empty,
            OidcResources = section
                .GetSection(nameof(BackendConsoleOptions.OidcResources))
                .GetChildren()
                .Select(item => item.Value?.Trim() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            NyxApiBaseUrl = section[nameof(BackendConsoleOptions.NyxApiBaseUrl)] ?? string.Empty,
            StorageKey = section[nameof(BackendConsoleOptions.StorageKey)] ?? string.Empty,
            DefaultReturnPath = section[nameof(BackendConsoleOptions.DefaultReturnPath)] ?? string.Empty,
        };
        ApplyFallbacks(configuration, options);
        ApplyHostEnvironmentOverrides(options);
        NormalizeOidcResources(configuration, options);
        return options;
    }

    private static void ApplyFallbacks(IConfiguration configuration, BackendConsoleOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OidcAuthority))
        {
            options.OidcAuthority =
                configuration["Aevatar:Authentication:Authority"]
                ?? configuration["Aevatar:NyxId:Authority"]
                ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(options.NyxApiBaseUrl))
        {
            options.NyxApiBaseUrl =
                configuration["Aevatar:NyxId:ApiBaseUrl"]
                ?? string.Empty;
        }
    }

    private static void ApplyHostEnvironmentOverrides(BackendConsoleOptions options)
    {
        options.OidcAuthority = EnvironmentOverride("HOST_BACKEND_CONSOLE_OIDC_AUTHORITY", options.OidcAuthority);
        options.OidcScope = EnvironmentOverride("HOST_BACKEND_CONSOLE_OIDC_SCOPE", options.OidcScope);
        options.NyxApiBaseUrl = EnvironmentOverride("HOST_BACKEND_CONSOLE_NYX_API_BASE_URL", options.NyxApiBaseUrl);
        options.StorageKey = EnvironmentOverride("HOST_BACKEND_CONSOLE_STORAGE_KEY", options.StorageKey);
        options.DefaultReturnPath = EnvironmentOverride("HOST_BACKEND_CONSOLE_DEFAULT_RETURN_PATH", options.DefaultReturnPath);
    }

    private static void NormalizeOidcResources(
        IConfiguration configuration,
        BackendConsoleOptions options)
    {
        options.NyxApiBaseUrl = options.NyxApiBaseUrl.Trim().TrimEnd('/');
        var resources = options.OidcResources
            .Select(resource => resource.Trim())
            .Where(resource => resource.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (string.IsNullOrWhiteSpace(options.NyxApiBaseUrl))
        {
            options.OidcResources = resources;
            return;
        }

        var requiredResource =
            $"{options.NyxApiBaseUrl}/api/v1/proxy/s/aevatar";
        var configuredOrnnServiceSlug = configuration["Aevatar:Ornn:NyxIdSlug"]?.Trim();
        var ornnServiceSlug = string.IsNullOrEmpty(configuredOrnnServiceSlug)
            ? "ornn-api"
            : configuredOrnnServiceSlug;
        var ornnResource =
            $"{options.NyxApiBaseUrl}/api/v1/proxy/s/{Uri.EscapeDataString(ornnServiceSlug)}";
        var legacyAuthorityResource = string.IsNullOrWhiteSpace(options.OidcAuthority)
            ? null
            : $"{options.OidcAuthority.Trim().TrimEnd('/')}/api/v1/proxy/s/aevatar";
        if (!string.Equals(legacyAuthorityResource, requiredResource, StringComparison.Ordinal))
        {
            resources = resources
                .Where(resource => !string.Equals(resource, legacyAuthorityResource, StringComparison.Ordinal))
                .ToArray();
        }

        options.OidcResources = new[] { requiredResource, ornnResource }
            .Concat(resources)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string EnvironmentOverride(string key, string configuredValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value)
            ? configuredValue
            : value.Trim();
    }
}
