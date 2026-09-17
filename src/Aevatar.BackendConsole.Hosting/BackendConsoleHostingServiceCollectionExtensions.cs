using Aevatar.Configuration;
using Aevatar.Configuration.BackendConsole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aevatar.BackendConsole.Hosting;

public static class BackendConsoleHostingServiceCollectionExtensions
{
    private const string OfflineAccessScope = "offline_access";

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
            NyxWebBaseUrl = section[nameof(BackendConsoleOptions.NyxWebBaseUrl)] ?? string.Empty,
            StorageKey = section[nameof(BackendConsoleOptions.StorageKey)] ?? string.Empty,
            DefaultReturnPath = section[nameof(BackendConsoleOptions.DefaultReturnPath)] ?? string.Empty,
            EnableStudioWireInspector = section.GetValue<bool>(
                nameof(BackendConsoleOptions.EnableStudioWireInspector)),
        };
        ApplyHostEnvironmentOverrides(options);
        ApplyFallbacks(configuration, options);
        NormalizeOidcScope(options);
        NormalizeOidcResources(configuration, options);
        return options;
    }

    private static void ApplyFallbacks(IConfiguration configuration, BackendConsoleOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OidcAuthority))
        {
            options.OidcAuthority =
                configuration["Aevatar:NyxId:Authority"]
                ?? configuration["Aevatar:Authentication:Authority"]
                ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(options.NyxApiBaseUrl))
        {
            options.NyxApiBaseUrl =
                NyxIdEndpointResolver.ResolvePublicApiBaseUrl(configuration)
                ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(options.NyxWebBaseUrl))
        {
            options.NyxWebBaseUrl =
                (!string.IsNullOrWhiteSpace(options.NyxApiBaseUrl)
                    ? options.NyxApiBaseUrl
                    : NyxIdEndpointResolver.ResolvePublicApiBaseUrl(configuration))
                ?? string.Empty;
        }
    }

    private static void ApplyHostEnvironmentOverrides(BackendConsoleOptions options)
    {
        options.OidcAuthority = EnvironmentOverride("HOST_BACKEND_CONSOLE_OIDC_AUTHORITY", options.OidcAuthority);
        options.OidcScope = EnvironmentOverride("HOST_BACKEND_CONSOLE_OIDC_SCOPE", options.OidcScope);
        options.NyxApiBaseUrl = EnvironmentOverride("HOST_BACKEND_CONSOLE_NYX_API_BASE_URL", options.NyxApiBaseUrl);
        options.NyxWebBaseUrl = EnvironmentOverride("HOST_BACKEND_CONSOLE_NYX_WEB_BASE_URL", options.NyxWebBaseUrl);
        options.StorageKey = EnvironmentOverride("HOST_BACKEND_CONSOLE_STORAGE_KEY", options.StorageKey);
        options.DefaultReturnPath = EnvironmentOverride("HOST_BACKEND_CONSOLE_DEFAULT_RETURN_PATH", options.DefaultReturnPath);
        options.EnableStudioWireInspector = EnvironmentOverride(
            "HOST_BACKEND_CONSOLE_ENABLE_STUDIO_WIRE_INSPECTOR",
            options.EnableStudioWireInspector);
    }

    private static void NormalizeOidcScope(BackendConsoleOptions options)
    {
        var scopes = options.OidcScope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (scopes.Length == 0)
            return;

        options.OidcScope = string.Join(
            ' ',
            scopes.Append(OfflineAccessScope).Distinct(StringComparer.Ordinal));
    }

    private static void NormalizeOidcResources(
        IConfiguration configuration,
        BackendConsoleOptions options)
    {
        options.NyxApiBaseUrl = options.NyxApiBaseUrl.Trim().TrimEnd('/');
        options.NyxWebBaseUrl = options.NyxWebBaseUrl.Trim().TrimEnd('/');
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

    private static bool EnvironmentOverride(string key, bool configuredValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return bool.TryParse(value, out var parsed) ? parsed : configuredValue;
    }
}
