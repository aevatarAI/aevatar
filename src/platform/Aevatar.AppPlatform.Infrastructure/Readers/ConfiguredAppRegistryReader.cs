using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Core.Services;
using Aevatar.AppPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Aevatar.AppPlatform.Infrastructure.Readers;

public sealed class ConfiguredAppRegistryReader : IAppRegistryReader
{
    private readonly IReadOnlyDictionary<string, AppDefinitionSnapshot> _apps;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, AppReleaseSnapshot>> _releasesByApp;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AppRouteSnapshot>> _routesByApp;
    private readonly IReadOnlyDictionary<string, AppRouteSnapshot> _routesByPath;

    public ConfiguredAppRegistryReader(IOptions<AppPlatformOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var apps = new Dictionary<string, AppDefinitionSnapshot>(StringComparer.Ordinal);
        var releasesByApp = new Dictionary<string, IReadOnlyDictionary<string, AppReleaseSnapshot>>(StringComparer.Ordinal);
        var routesByApp = new Dictionary<string, IReadOnlyList<AppRouteSnapshot>>(StringComparer.Ordinal);
        var routesByPath = new Dictionary<string, AppRouteSnapshot>(StringComparer.Ordinal);

        foreach (var appOptions in options.Value.Apps)
        {
            var appId = NormalizeRequired(appOptions.AppId, nameof(appOptions.AppId));
            if (!apps.TryAdd(appId, BuildDefinition(appOptions, appId)))
                throw new InvalidOperationException($"Duplicate AppPlatform app id '{appId}'.");

            var releases = BuildReleases(appOptions, appId);
            releasesByApp[appId] = releases;

            if (releases.Count > 0 &&
                !string.IsNullOrWhiteSpace(appOptions.DefaultReleaseId) &&
                !releases.ContainsKey(appOptions.DefaultReleaseId.Trim()))
            {
                throw new InvalidOperationException(
                    $"AppPlatform app '{appId}' declares default release '{appOptions.DefaultReleaseId}' that does not exist.");
            }

            var routes = BuildRoutes(appOptions, appId, releases);
            routesByApp[appId] = routes;
            foreach (var route in routes)
            {
                if (!routesByPath.TryAdd(route.RoutePath, route))
                    throw new InvalidOperationException($"Duplicate AppPlatform route '{route.RoutePath}'.");
            }
        }

        _apps = apps;
        _releasesByApp = releasesByApp;
        _routesByApp = routesByApp;
        _routesByPath = routesByPath;
    }

    public Task<IReadOnlyList<AppDefinitionSnapshot>> ListAppsAsync(string? ownerScopeId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedOwnerScopeId = ownerScopeId?.Trim();
        var apps = _apps.Values
            .Where(x => string.IsNullOrWhiteSpace(normalizedOwnerScopeId) ||
                        string.Equals(x.OwnerScopeId, normalizedOwnerScopeId, StringComparison.Ordinal))
            .OrderBy(x => x.AppId, StringComparer.Ordinal)
            .Select(static x => x.Clone())
            .ToArray();
        return Task.FromResult<IReadOnlyList<AppDefinitionSnapshot>>(apps);
    }

    public Task<AppDefinitionSnapshot?> GetAppAsync(string appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        return Task.FromResult(_apps.TryGetValue(normalizedAppId, out var snapshot) ? snapshot.Clone() : null);
    }

    public Task<IReadOnlyList<AppReleaseSnapshot>> ListReleasesAsync(string appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        if (!_releasesByApp.TryGetValue(normalizedAppId, out var releases))
            return Task.FromResult<IReadOnlyList<AppReleaseSnapshot>>([]);

        return Task.FromResult<IReadOnlyList<AppReleaseSnapshot>>(releases.Values
            .OrderBy(x => x.ReleaseId, StringComparer.Ordinal)
            .Select(static x => x.Clone())
            .ToArray());
    }

    public Task<AppReleaseSnapshot?> GetReleaseAsync(string appId, string releaseId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        var normalizedReleaseId = NormalizeRequired(releaseId, nameof(releaseId));
        if (!_releasesByApp.TryGetValue(normalizedAppId, out var releases))
            return Task.FromResult<AppReleaseSnapshot?>(null);

        return Task.FromResult(releases.TryGetValue(normalizedReleaseId, out var release) ? release.Clone() : null);
    }

    public Task<IReadOnlyList<AppRouteSnapshot>> ListRoutesAsync(string appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        if (!_routesByApp.TryGetValue(normalizedAppId, out var routes))
            return Task.FromResult<IReadOnlyList<AppRouteSnapshot>>([]);

        return Task.FromResult<IReadOnlyList<AppRouteSnapshot>>(routes
            .OrderBy(x => x.RoutePath, StringComparer.Ordinal)
            .Select(static x => x.Clone())
            .ToArray());
    }

    public Task<AppRouteSnapshot?> GetRouteByPathAsync(string routePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedRoutePath = AppRoutePathNormalizer.NormalizeRequired(routePath, nameof(routePath));
        return Task.FromResult(_routesByPath.TryGetValue(normalizedRoutePath, out var route) ? route.Clone() : null);
    }

    private static AppDefinitionSnapshot BuildDefinition(ConfiguredAppDefinitionOptions options, string appId)
    {
        var definition = new AppDefinitionSnapshot
        {
            AppId = appId,
            OwnerScopeId = options.OwnerScopeId?.Trim() ?? string.Empty,
            DisplayName = string.IsNullOrWhiteSpace(options.DisplayName) ? appId : options.DisplayName.Trim(),
            Description = options.Description?.Trim() ?? string.Empty,
            Visibility = ParseVisibility(options.Visibility),
            DefaultReleaseId = options.DefaultReleaseId?.Trim() ?? string.Empty,
        };

        foreach (var route in options.Routes)
        {
            definition.RoutePaths.Add(AppRoutePathNormalizer.NormalizeRequired(route.RoutePath, nameof(route.RoutePath)));
        }

        return definition;
    }

    private static IReadOnlyDictionary<string, AppReleaseSnapshot> BuildReleases(
        ConfiguredAppDefinitionOptions options,
        string appId)
    {
        var releases = new Dictionary<string, AppReleaseSnapshot>(StringComparer.Ordinal);
        foreach (var releaseOptions in options.Releases)
        {
            var releaseId = NormalizeRequired(releaseOptions.ReleaseId, nameof(releaseOptions.ReleaseId));
            if (releases.ContainsKey(releaseId))
                throw new InvalidOperationException($"Duplicate release '{releaseId}' for app '{appId}'.");

            var release = new AppReleaseSnapshot
            {
                AppId = appId,
                ReleaseId = releaseId,
                DisplayName = string.IsNullOrWhiteSpace(releaseOptions.DisplayName) ? releaseId : releaseOptions.DisplayName.Trim(),
                Status = ParseReleaseStatus(releaseOptions.Status),
            };

            foreach (var serviceOptions in releaseOptions.Services)
            {
                release.ServiceRefs.Add(new AppServiceRef
                {
                    TenantId = serviceOptions.TenantId?.Trim() ?? string.Empty,
                    AppId = serviceOptions.AppId?.Trim() ?? string.Empty,
                    Namespace = serviceOptions.Namespace?.Trim() ?? string.Empty,
                    ServiceId = NormalizeRequired(serviceOptions.ServiceId, nameof(serviceOptions.ServiceId)),
                    RevisionId = serviceOptions.RevisionId?.Trim() ?? string.Empty,
                    ImplementationKind = ParseImplementationKind(serviceOptions.ImplementationKind),
                    Role = ParseServiceRole(serviceOptions.Role),
                });
            }

            foreach (var entryOptions in releaseOptions.Entries)
            {
                var serviceId = NormalizeRequired(entryOptions.ServiceId, nameof(entryOptions.ServiceId));
                var entryId = NormalizeRequired(entryOptions.EntryId, nameof(entryOptions.EntryId));
                var serviceRef = release.ServiceRefs.FirstOrDefault(x => string.Equals(x.ServiceId, serviceId, StringComparison.Ordinal));
                if (serviceRef == null)
                {
                    throw new InvalidOperationException(
                        $"Entry '{entryId}' in app '{appId}' release '{releaseId}' points to unknown service '{serviceId}'.");
                }

                if (serviceRef.Role != AppServiceRole.Entry)
                {
                    throw new InvalidOperationException(
                        $"Entry '{entryId}' in app '{appId}' release '{releaseId}' points to service '{serviceId}' that is not marked as entry.");
                }

                release.EntryRefs.Add(new AppEntryRef
                {
                    EntryId = entryId,
                    ServiceId = serviceId,
                    EndpointId = NormalizeRequired(entryOptions.EndpointId, nameof(entryOptions.EndpointId)),
                });
            }

            releases.Add(releaseId, release);
        }

        return releases;
    }

    private static IReadOnlyList<AppRouteSnapshot> BuildRoutes(
        ConfiguredAppDefinitionOptions options,
        string appId,
        IReadOnlyDictionary<string, AppReleaseSnapshot> releases)
    {
        var routes = new List<AppRouteSnapshot>();
        foreach (var routeOptions in options.Routes)
        {
            var routePath = AppRoutePathNormalizer.NormalizeRequired(routeOptions.RoutePath, nameof(routeOptions.RoutePath));
            var releaseId = string.IsNullOrWhiteSpace(routeOptions.ReleaseId)
                ? options.DefaultReleaseId?.Trim() ?? string.Empty
                : routeOptions.ReleaseId.Trim();
            if (string.IsNullOrWhiteSpace(releaseId))
            {
                throw new InvalidOperationException(
                    $"App '{appId}' route '{routePath}' requires a release, but no route release or default release was configured.");
            }

            if (!releases.TryGetValue(releaseId, out var release))
            {
                throw new InvalidOperationException(
                    $"App '{appId}' route '{routePath}' points to unknown release '{releaseId}'.");
            }

            var entryId = string.IsNullOrWhiteSpace(routeOptions.EntryId)
                ? release.EntryRefs.FirstOrDefault()?.EntryId ?? string.Empty
                : routeOptions.EntryId.Trim();
            if (string.IsNullOrWhiteSpace(entryId))
            {
                throw new InvalidOperationException(
                    $"App '{appId}' route '{routePath}' requires an entry, but release '{releaseId}' has no entries.");
            }

            if (!release.EntryRefs.Any(x => string.Equals(x.EntryId, entryId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"App '{appId}' route '{routePath}' points to unknown entry '{entryId}' in release '{releaseId}'.");
            }

            routes.Add(new AppRouteSnapshot
            {
                RoutePath = routePath,
                AppId = appId,
                ReleaseId = releaseId,
                EntryId = entryId,
            });
        }

        return routes;
    }

    private static AppVisibility ParseVisibility(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "public" => AppVisibility.Public,
            "private" or "" or null => AppVisibility.Private,
            _ => throw new InvalidOperationException($"Unsupported AppPlatform visibility '{rawValue}'."),
        };
    }

    private static AppReleaseStatus ParseReleaseStatus(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "draft" or "" or null => AppReleaseStatus.Draft,
            "published" => AppReleaseStatus.Published,
            "archived" => AppReleaseStatus.Archived,
            _ => throw new InvalidOperationException($"Unsupported AppPlatform release status '{rawValue}'."),
        };
    }

    private static AppImplementationKind ParseImplementationKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "static" => AppImplementationKind.Static,
            "scripting" => AppImplementationKind.Scripting,
            "workflow" => AppImplementationKind.Workflow,
            _ => throw new InvalidOperationException($"Unsupported AppPlatform implementation kind '{rawValue}'."),
        };
    }

    private static AppServiceRole ParseServiceRole(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "entry" => AppServiceRole.Entry,
            "companion" => AppServiceRole.Companion,
            "internal" or "" or null => AppServiceRole.Internal,
            _ => throw new InvalidOperationException($"Unsupported AppPlatform service role '{rawValue}'."),
        };
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}
