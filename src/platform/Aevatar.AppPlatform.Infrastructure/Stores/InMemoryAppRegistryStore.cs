using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Core.Services;
using Aevatar.AppPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Aevatar.AppPlatform.Infrastructure.Stores;

public sealed class InMemoryAppRegistryStore : IAppRegistryReader, IAppRegistryWriter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AppDefinitionSnapshot> _apps;
    private readonly Dictionary<string, Dictionary<string, AppReleaseSnapshot>> _releasesByApp;
    private readonly Dictionary<string, List<AppRouteSnapshot>> _routesByApp;
    private readonly Dictionary<string, AppRouteSnapshot> _routesByPath;

    public InMemoryAppRegistryStore(IOptions<AppPlatformOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _apps = new Dictionary<string, AppDefinitionSnapshot>(StringComparer.Ordinal);
        _releasesByApp = new Dictionary<string, Dictionary<string, AppReleaseSnapshot>>(StringComparer.Ordinal);
        _routesByApp = new Dictionary<string, List<AppRouteSnapshot>>(StringComparer.Ordinal);
        _routesByPath = new Dictionary<string, AppRouteSnapshot>(StringComparer.Ordinal);

        foreach (var appOptions in options.Value.Apps)
        {
            var appId = NormalizeRequired(appOptions.AppId, nameof(appOptions.AppId));
            if (!_apps.TryAdd(appId, BuildDefinition(appOptions, appId)))
                throw new InvalidOperationException($"Duplicate AppPlatform app id '{appId}'.");

            var releases = BuildReleases(appOptions, appId);
            _releasesByApp[appId] = releases;

            if (releases.Count > 0 &&
                !string.IsNullOrWhiteSpace(appOptions.DefaultReleaseId) &&
                !releases.ContainsKey(appOptions.DefaultReleaseId.Trim()))
            {
                throw new InvalidOperationException(
                    $"AppPlatform app '{appId}' declares default release '{appOptions.DefaultReleaseId}' that does not exist.");
            }

            var routes = BuildRoutes(appOptions, appId, releases);
            _routesByApp[appId] = routes;
            foreach (var route in routes)
            {
                if (!_routesByPath.TryAdd(route.RoutePath, route.Clone()))
                    throw new InvalidOperationException($"Duplicate AppPlatform route '{route.RoutePath}'.");
            }

            SyncAppRoutePathsNoLock(appId);
        }
    }

    public Task<IReadOnlyList<AppDefinitionSnapshot>> ListAppsAsync(string? ownerScopeId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var normalizedOwnerScopeId = ownerScopeId?.Trim();
            var apps = _apps.Values
                .Where(x => string.IsNullOrWhiteSpace(normalizedOwnerScopeId) ||
                            string.Equals(x.OwnerScopeId, normalizedOwnerScopeId, StringComparison.Ordinal))
                .OrderBy(x => x.AppId, StringComparer.Ordinal)
                .Select(static x => x.Clone())
                .ToArray();
            return Task.FromResult<IReadOnlyList<AppDefinitionSnapshot>>(apps);
        }
    }

    public Task<AppDefinitionSnapshot?> GetAppAsync(string appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        lock (_gate)
        {
            return Task.FromResult(_apps.TryGetValue(normalizedAppId, out var snapshot) ? snapshot.Clone() : null);
        }
    }

    public Task<IReadOnlyList<AppReleaseSnapshot>> ListReleasesAsync(string appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        lock (_gate)
        {
            if (!_releasesByApp.TryGetValue(normalizedAppId, out var releases))
                return Task.FromResult<IReadOnlyList<AppReleaseSnapshot>>([]);

            return Task.FromResult<IReadOnlyList<AppReleaseSnapshot>>(releases.Values
                .OrderBy(x => x.ReleaseId, StringComparer.Ordinal)
                .Select(static x => x.Clone())
                .ToArray());
        }
    }

    public Task<AppReleaseSnapshot?> GetReleaseAsync(string appId, string releaseId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        var normalizedReleaseId = NormalizeRequired(releaseId, nameof(releaseId));
        lock (_gate)
        {
            if (!_releasesByApp.TryGetValue(normalizedAppId, out var releases))
                return Task.FromResult<AppReleaseSnapshot?>(null);

            return Task.FromResult(releases.TryGetValue(normalizedReleaseId, out var release) ? release.Clone() : null);
        }
    }

    public Task<IReadOnlyList<AppRouteSnapshot>> ListRoutesAsync(string appId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        lock (_gate)
        {
            if (!_routesByApp.TryGetValue(normalizedAppId, out var routes))
                return Task.FromResult<IReadOnlyList<AppRouteSnapshot>>([]);

            return Task.FromResult<IReadOnlyList<AppRouteSnapshot>>(routes
                .OrderBy(x => x.RoutePath, StringComparer.Ordinal)
                .Select(static x => x.Clone())
                .ToArray());
        }
    }

    public Task<AppRouteSnapshot?> GetRouteByPathAsync(string routePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedRoutePath = AppRoutePathNormalizer.NormalizeRequired(routePath, nameof(routePath));
        lock (_gate)
        {
            return Task.FromResult(_routesByPath.TryGetValue(normalizedRoutePath, out var route) ? route.Clone() : null);
        }
    }

    public Task<AppDefinitionSnapshot> CreateAppAsync(AppDefinitionSnapshot definition, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(definition);

        var normalizedDefinition = definition.Clone();
        lock (_gate)
        {
            if (_apps.ContainsKey(normalizedDefinition.AppId))
                throw new InvalidOperationException($"App '{normalizedDefinition.AppId}' already exists.");

            _apps[normalizedDefinition.AppId] = normalizedDefinition;
            _releasesByApp[normalizedDefinition.AppId] = new Dictionary<string, AppReleaseSnapshot>(StringComparer.Ordinal);
            _routesByApp[normalizedDefinition.AppId] = [];
            SyncAppRoutePathsNoLock(normalizedDefinition.AppId);
            return Task.FromResult(normalizedDefinition.Clone());
        }
    }

    public Task<AppDefinitionSnapshot> UpsertAppAsync(AppDefinitionSnapshot definition, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(definition);

        var normalizedDefinition = definition.Clone();
        lock (_gate)
        {
            var existingDefaultReleaseId = string.Empty;
            var existingOwnerScopeId = normalizedDefinition.OwnerScopeId;
            var existingVisibility = normalizedDefinition.Visibility;
            if (_apps.TryGetValue(normalizedDefinition.AppId, out var existing))
            {
                existingDefaultReleaseId = existing.DefaultReleaseId;
                existingOwnerScopeId = existing.OwnerScopeId;
                existingVisibility = existing.Visibility;
            }

            if (!_releasesByApp.ContainsKey(normalizedDefinition.AppId))
                _releasesByApp[normalizedDefinition.AppId] = new Dictionary<string, AppReleaseSnapshot>(StringComparer.Ordinal);
            if (!_routesByApp.ContainsKey(normalizedDefinition.AppId))
                _routesByApp[normalizedDefinition.AppId] = [];

            if (!string.IsNullOrWhiteSpace(normalizedDefinition.DefaultReleaseId) &&
                !_releasesByApp[normalizedDefinition.AppId].ContainsKey(normalizedDefinition.DefaultReleaseId))
            {
                throw new InvalidOperationException(
                    $"App '{normalizedDefinition.AppId}' declares default release '{normalizedDefinition.DefaultReleaseId}' that does not exist.");
            }

            normalizedDefinition.OwnerScopeId = string.IsNullOrWhiteSpace(normalizedDefinition.OwnerScopeId)
                ? existingOwnerScopeId
                : normalizedDefinition.OwnerScopeId;
            normalizedDefinition.Visibility = normalizedDefinition.Visibility == AppVisibility.Unspecified
                ? existingVisibility == AppVisibility.Unspecified ? AppVisibility.Private : existingVisibility
                : normalizedDefinition.Visibility;
            normalizedDefinition.DefaultReleaseId = string.IsNullOrWhiteSpace(normalizedDefinition.DefaultReleaseId)
                ? existingDefaultReleaseId
                : normalizedDefinition.DefaultReleaseId;
            _apps[normalizedDefinition.AppId] = normalizedDefinition;
            SyncAppRoutePathsNoLock(normalizedDefinition.AppId);
            return Task.FromResult(normalizedDefinition.Clone());
        }
    }

    public Task<AppDefinitionSnapshot> SetDefaultReleaseAsync(string appId, string releaseId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        var normalizedReleaseId = NormalizeRequired(releaseId, nameof(releaseId));
        lock (_gate)
        {
            var app = GetRequiredAppNoLock(normalizedAppId);
            var release = GetRequiredReleaseNoLock(normalizedAppId, normalizedReleaseId);
            if (release.Status != AppReleaseStatus.Published)
            {
                throw new InvalidOperationException(
                    $"App '{normalizedAppId}' release '{normalizedReleaseId}' must be published before it can become the default release.");
            }

            app.DefaultReleaseId = normalizedReleaseId;
            return Task.FromResult(app.Clone());
        }
    }

    public Task<AppReleaseSnapshot> UpsertReleaseAsync(AppReleaseSnapshot release, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(release);

        var normalizedRelease = release.Clone();
        lock (_gate)
        {
            GetRequiredAppNoLock(normalizedRelease.AppId);
            ValidateReleaseNoLock(normalizedRelease);
            var releases = _releasesByApp[normalizedRelease.AppId];
            releases[normalizedRelease.ReleaseId] = normalizedRelease;
            return Task.FromResult(normalizedRelease.Clone());
        }
    }

    public Task<AppReleaseSnapshot> UpdateReleaseStatusAsync(
        string appId,
        string releaseId,
        AppReleaseStatus status,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        var normalizedReleaseId = NormalizeRequired(releaseId, nameof(releaseId));
        lock (_gate)
        {
            var app = GetRequiredAppNoLock(normalizedAppId);
            var release = GetRequiredReleaseNoLock(normalizedAppId, normalizedReleaseId);
            if (status == AppReleaseStatus.Archived &&
                string.Equals(app.DefaultReleaseId, normalizedReleaseId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"App '{normalizedAppId}' default release '{normalizedReleaseId}' cannot be archived. Move the default release first.");
            }

            release.Status = status;
            return Task.FromResult(release.Clone());
        }
    }

    public Task<AppEntryRef> UpsertEntryAsync(
        string appId,
        string releaseId,
        AppEntryRef entry,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        var normalizedReleaseId = NormalizeRequired(releaseId, nameof(releaseId));
        var normalizedEntry = entry.Clone();
        lock (_gate)
        {
            var release = GetRequiredReleaseNoLock(normalizedAppId, normalizedReleaseId);
            var service = release.ServiceRefs.FirstOrDefault(x => string.Equals(x.ServiceId, normalizedEntry.ServiceId, StringComparison.Ordinal));
            if (service == null)
            {
                throw new InvalidOperationException(
                    $"Function '{normalizedEntry.EntryId}' in app '{normalizedAppId}' release '{normalizedReleaseId}' points to unknown service '{normalizedEntry.ServiceId}'.");
            }

            if (service.Role != AppServiceRole.Entry)
            {
                throw new InvalidOperationException(
                    $"Function '{normalizedEntry.EntryId}' in app '{normalizedAppId}' release '{normalizedReleaseId}' points to service '{normalizedEntry.ServiceId}' that is not marked as entry.");
            }

            var existing = release.EntryRefs.FirstOrDefault(x => string.Equals(x.EntryId, normalizedEntry.EntryId, StringComparison.Ordinal));
            if (existing == null)
            {
                release.EntryRefs.Add(normalizedEntry);
            }
            else
            {
                existing.ServiceId = normalizedEntry.ServiceId;
                existing.EndpointId = normalizedEntry.EndpointId;
            }

            return Task.FromResult(normalizedEntry.Clone());
        }
    }

    public Task<bool> DeleteEntryAsync(
        string appId,
        string releaseId,
        string entryId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        var normalizedReleaseId = NormalizeRequired(releaseId, nameof(releaseId));
        var normalizedEntryId = NormalizeRequired(entryId, nameof(entryId));
        lock (_gate)
        {
            var release = GetRequiredReleaseNoLock(normalizedAppId, normalizedReleaseId);
            if (_routesByApp.TryGetValue(normalizedAppId, out var routes) &&
                routes.Any(x => string.Equals(x.ReleaseId, normalizedReleaseId, StringComparison.Ordinal) &&
                                string.Equals(x.EntryId, normalizedEntryId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Function '{normalizedEntryId}' in app '{normalizedAppId}' release '{normalizedReleaseId}' is still referenced by at least one route.");
            }

            var existing = release.EntryRefs.FirstOrDefault(x => string.Equals(x.EntryId, normalizedEntryId, StringComparison.Ordinal));
            if (existing == null)
                return Task.FromResult(false);

            release.EntryRefs.Remove(existing);
            return Task.FromResult(true);
        }
    }

    public Task<AppReleaseResourcesSnapshot> ReplaceReleaseResourcesAsync(
        AppReleaseResourcesSnapshot resources,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(resources);

        var normalizedResources = resources.Clone();
        lock (_gate)
        {
            var release = GetRequiredReleaseNoLock(normalizedResources.AppId, normalizedResources.ReleaseId);
            release.ConnectorRefs.Clear();
            release.ConnectorRefs.Add(normalizedResources.ConnectorRefs.Select(static x => x.Clone()));
            release.SecretRefs.Clear();
            release.SecretRefs.Add(normalizedResources.SecretRefs.Select(static x => x.Clone()));
            return Task.FromResult(new AppReleaseResourcesSnapshot
            {
                AppId = normalizedResources.AppId,
                ReleaseId = normalizedResources.ReleaseId,
                ConnectorRefs = { release.ConnectorRefs.Select(static x => x.Clone()) },
                SecretRefs = { release.SecretRefs.Select(static x => x.Clone()) },
            });
        }
    }

    public Task<AppRouteSnapshot> UpsertRouteAsync(AppRouteSnapshot route, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(route);

        var normalizedRoute = route.Clone();
        lock (_gate)
        {
            GetRequiredAppNoLock(normalizedRoute.AppId);
            var release = GetRequiredReleaseNoLock(normalizedRoute.AppId, normalizedRoute.ReleaseId);
            if (!release.EntryRefs.Any(x => string.Equals(x.EntryId, normalizedRoute.EntryId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"App '{normalizedRoute.AppId}' route '{normalizedRoute.RoutePath}' points to unknown function '{normalizedRoute.EntryId}' in release '{normalizedRoute.ReleaseId}'.");
            }

            if (_routesByPath.TryGetValue(normalizedRoute.RoutePath, out var existingRoute) &&
                !string.Equals(existingRoute.AppId, normalizedRoute.AppId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"App route '{normalizedRoute.RoutePath}' is already claimed by app '{existingRoute.AppId}'.");
            }

            if (!_routesByApp.TryGetValue(normalizedRoute.AppId, out var routes))
            {
                routes = [];
                _routesByApp[normalizedRoute.AppId] = routes;
            }

            var priorRoute = routes.FirstOrDefault(x => string.Equals(x.RoutePath, normalizedRoute.RoutePath, StringComparison.Ordinal));
            if (priorRoute == null)
            {
                routes.Add(normalizedRoute);
            }
            else
            {
                priorRoute.ReleaseId = normalizedRoute.ReleaseId;
                priorRoute.EntryId = normalizedRoute.EntryId;
                normalizedRoute = priorRoute;
            }

            _routesByPath[normalizedRoute.RoutePath] = normalizedRoute.Clone();
            SyncAppRoutePathsNoLock(normalizedRoute.AppId);
            return Task.FromResult(normalizedRoute.Clone());
        }
    }

    public Task<bool> DeleteRouteAsync(string appId, string routePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        var normalizedRoutePath = AppRoutePathNormalizer.NormalizeRequired(routePath, nameof(routePath));
        lock (_gate)
        {
            if (!_routesByPath.TryGetValue(normalizedRoutePath, out var route))
                return Task.FromResult(false);

            if (!string.Equals(route.AppId, normalizedAppId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"App route '{normalizedRoutePath}' does not belong to app '{normalizedAppId}'.");
            }

            _routesByPath.Remove(normalizedRoutePath);
            if (_routesByApp.TryGetValue(normalizedAppId, out var routes))
            {
                var index = routes.FindIndex(x => string.Equals(x.RoutePath, normalizedRoutePath, StringComparison.Ordinal));
                if (index >= 0)
                    routes.RemoveAt(index);
            }

            SyncAppRoutePathsNoLock(normalizedAppId);
            return Task.FromResult(true);
        }
    }

    private AppDefinitionSnapshot GetRequiredAppNoLock(string appId)
    {
        if (!_apps.TryGetValue(appId, out var app))
            throw new InvalidOperationException($"Unknown app '{appId}'.");

        return app;
    }

    private AppReleaseSnapshot GetRequiredReleaseNoLock(string appId, string releaseId)
    {
        if (!_releasesByApp.TryGetValue(appId, out var releases) || !releases.TryGetValue(releaseId, out var release))
            throw new InvalidOperationException($"Unknown app release '{appId}/{releaseId}'.");

        return release;
    }

    private void ValidateReleaseNoLock(AppReleaseSnapshot release)
    {
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in release.ServiceRefs)
        {
            if (!serviceIds.Add(service.ServiceId))
            {
                throw new InvalidOperationException(
                    $"Duplicate service '{service.ServiceId}' for app '{release.AppId}' release '{release.ReleaseId}'.");
            }
        }

        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in release.EntryRefs)
        {
            if (!entryIds.Add(entry.EntryId))
            {
                throw new InvalidOperationException(
                    $"Duplicate function '{entry.EntryId}' for app '{release.AppId}' release '{release.ReleaseId}'.");
            }

            var service = release.ServiceRefs.FirstOrDefault(x => string.Equals(x.ServiceId, entry.ServiceId, StringComparison.Ordinal));
            if (service == null)
            {
                throw new InvalidOperationException(
                    $"Function '{entry.EntryId}' in app '{release.AppId}' release '{release.ReleaseId}' points to unknown service '{entry.ServiceId}'.");
            }

            if (service.Role != AppServiceRole.Entry)
            {
                throw new InvalidOperationException(
                    $"Function '{entry.EntryId}' in app '{release.AppId}' release '{release.ReleaseId}' points to service '{entry.ServiceId}' that is not marked as entry.");
            }
        }

        ValidateUniqueReleaseResourcesNoLock(release);
    }

    private static void ValidateUniqueReleaseResourcesNoLock(AppReleaseSnapshot release)
    {
        var connectorResourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connector in release.ConnectorRefs)
        {
            if (!connectorResourceIds.Add(connector.ResourceId))
            {
                throw new InvalidOperationException(
                    $"Duplicate connector resource '{connector.ResourceId}' for app '{release.AppId}' release '{release.ReleaseId}'.");
            }
        }

        var secretResourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var secret in release.SecretRefs)
        {
            if (!secretResourceIds.Add(secret.ResourceId))
            {
                throw new InvalidOperationException(
                    $"Duplicate secret resource '{secret.ResourceId}' for app '{release.AppId}' release '{release.ReleaseId}'.");
            }
        }
    }

    private void SyncAppRoutePathsNoLock(string appId)
    {
        if (!_apps.TryGetValue(appId, out var app))
            return;

        app.RoutePaths.Clear();
        if (_routesByApp.TryGetValue(appId, out var routes))
        {
            app.RoutePaths.Add(routes
                .Select(static x => x.RoutePath)
                .OrderBy(static x => x, StringComparer.Ordinal));
        }
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

    private static Dictionary<string, AppReleaseSnapshot> BuildReleases(
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

            var connectorResourceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var connectorOptions in releaseOptions.Connectors)
            {
                var resourceId = NormalizeRequired(connectorOptions.ResourceId, nameof(connectorOptions.ResourceId));
                if (!connectorResourceIds.Add(resourceId))
                {
                    throw new InvalidOperationException(
                        $"Duplicate connector resource '{resourceId}' for app '{appId}' release '{releaseId}'.");
                }

                release.ConnectorRefs.Add(new AppConnectorRef
                {
                    ResourceId = resourceId,
                    ConnectorName = NormalizeRequired(connectorOptions.ConnectorName, nameof(connectorOptions.ConnectorName)),
                });
            }

            var secretResourceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var secretOptions in releaseOptions.Secrets)
            {
                var resourceId = NormalizeRequired(secretOptions.ResourceId, nameof(secretOptions.ResourceId));
                if (!secretResourceIds.Add(resourceId))
                {
                    throw new InvalidOperationException(
                        $"Duplicate secret resource '{resourceId}' for app '{appId}' release '{releaseId}'.");
                }

                release.SecretRefs.Add(new AppSecretRef
                {
                    ResourceId = resourceId,
                    SecretName = NormalizeRequired(secretOptions.SecretName, nameof(secretOptions.SecretName)),
                });
            }

            releases.Add(releaseId, release);
        }

        return releases;
    }

    private static List<AppRouteSnapshot> BuildRoutes(
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
