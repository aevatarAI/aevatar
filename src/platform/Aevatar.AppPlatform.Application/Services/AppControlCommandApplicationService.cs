using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Core.Services;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class AppControlCommandApplicationService : IAppControlCommandPort
{
    private readonly IAppRegistryReader _registryReader;
    private readonly IAppRegistryWriter _registryWriter;

    public AppControlCommandApplicationService(
        IAppRegistryReader registryReader,
        IAppRegistryWriter registryWriter)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        _registryWriter = registryWriter ?? throw new ArgumentNullException(nameof(registryWriter));
    }

    public async Task<AppDefinitionSnapshot> CreateAppAsync(
        AppDefinitionSnapshot definition,
        CancellationToken ct = default)
    {
        var normalized = NormalizeDefinition(definition, preserveRoutePaths: false);
        var existing = await _registryReader.GetAppAsync(normalized.AppId, ct);
        if (existing != null)
            throw new InvalidOperationException($"App '{normalized.AppId}' already exists.");

        if (!string.IsNullOrWhiteSpace(normalized.DefaultReleaseId))
        {
            throw new InvalidOperationException(
                $"App '{normalized.AppId}' cannot declare default release '{normalized.DefaultReleaseId}' before that release exists.");
        }

        return await _registryWriter.CreateAppAsync(normalized, ct);
    }

    public Task<AppDefinitionSnapshot> UpsertAppAsync(
        AppDefinitionSnapshot definition,
        CancellationToken ct = default)
    {
        return UpsertAppAsyncCore(definition, ct);
    }

    public Task<AppDefinitionSnapshot> SetDefaultReleaseAsync(
        string appId,
        string releaseId,
        CancellationToken ct = default)
    {
        return _registryWriter.SetDefaultReleaseAsync(
            NormalizeRequired(appId, nameof(appId)),
            NormalizeRequired(releaseId, nameof(releaseId)),
            ct);
    }

    public Task<AppReleaseSnapshot> UpsertReleaseAsync(
        AppReleaseSnapshot release,
        CancellationToken ct = default)
    {
        var normalized = NormalizeRelease(release);
        return _registryWriter.UpsertReleaseAsync(normalized, ct);
    }

    public Task<AppReleaseSnapshot> PublishReleaseAsync(
        string appId,
        string releaseId,
        CancellationToken ct = default)
    {
        return _registryWriter.UpdateReleaseStatusAsync(
            NormalizeRequired(appId, nameof(appId)),
            NormalizeRequired(releaseId, nameof(releaseId)),
            AppReleaseStatus.Published,
            ct);
    }

    public Task<AppReleaseSnapshot> ArchiveReleaseAsync(
        string appId,
        string releaseId,
        CancellationToken ct = default)
    {
        return _registryWriter.UpdateReleaseStatusAsync(
            NormalizeRequired(appId, nameof(appId)),
            NormalizeRequired(releaseId, nameof(releaseId)),
            AppReleaseStatus.Archived,
            ct);
    }

    public Task<AppEntryRef> UpsertFunctionAsync(
        string appId,
        string releaseId,
        AppEntryRef functionRef,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(functionRef);

        var normalized = new AppEntryRef
        {
            EntryId = NormalizeRequired(functionRef.EntryId, nameof(functionRef.EntryId)),
            ServiceId = NormalizeRequired(functionRef.ServiceId, nameof(functionRef.ServiceId)),
            EndpointId = NormalizeRequired(functionRef.EndpointId, nameof(functionRef.EndpointId)),
        };
        return _registryWriter.UpsertEntryAsync(
            NormalizeRequired(appId, nameof(appId)),
            NormalizeRequired(releaseId, nameof(releaseId)),
            normalized,
            ct);
    }

    public Task<bool> DeleteFunctionAsync(
        string appId,
        string releaseId,
        string functionId,
        CancellationToken ct = default)
    {
        return _registryWriter.DeleteEntryAsync(
            NormalizeRequired(appId, nameof(appId)),
            NormalizeRequired(releaseId, nameof(releaseId)),
            NormalizeRequired(functionId, nameof(functionId)),
            ct);
    }

    public Task<AppReleaseResourcesSnapshot> ReplaceReleaseResourcesAsync(
        AppReleaseResourcesSnapshot resources,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var normalized = new AppReleaseResourcesSnapshot
        {
            AppId = NormalizeRequired(resources.AppId, nameof(resources.AppId)),
            ReleaseId = NormalizeRequired(resources.ReleaseId, nameof(resources.ReleaseId)),
        };
        normalized.ConnectorRefs.Add(resources.ConnectorRefs.Select(NormalizeConnector));
        normalized.SecretRefs.Add(resources.SecretRefs.Select(NormalizeSecret));
        EnsureUniqueConnectorResourceIds(normalized.ConnectorRefs, normalized.AppId, normalized.ReleaseId);
        EnsureUniqueSecretResourceIds(normalized.SecretRefs, normalized.AppId, normalized.ReleaseId);
        return _registryWriter.ReplaceReleaseResourcesAsync(normalized, ct);
    }

    public Task<AppRouteSnapshot> UpsertRouteAsync(
        AppRouteSnapshot route,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(route);

        var normalized = new AppRouteSnapshot
        {
            AppId = NormalizeRequired(route.AppId, nameof(route.AppId)),
            ReleaseId = NormalizeRequired(route.ReleaseId, nameof(route.ReleaseId)),
            EntryId = NormalizeRequired(route.EntryId, nameof(route.EntryId)),
            RoutePath = AppRoutePathNormalizer.NormalizeRequired(route.RoutePath, nameof(route.RoutePath)),
        };
        return _registryWriter.UpsertRouteAsync(normalized, ct);
    }

    public Task<bool> DeleteRouteAsync(
        string appId,
        string routePath,
        CancellationToken ct = default)
    {
        return _registryWriter.DeleteRouteAsync(
            NormalizeRequired(appId, nameof(appId)),
            AppRoutePathNormalizer.NormalizeRequired(routePath, nameof(routePath)),
            ct);
    }

    private async Task<AppDefinitionSnapshot> UpsertAppAsyncCore(
        AppDefinitionSnapshot definition,
        CancellationToken ct)
    {
        var normalized = NormalizeDefinition(definition, preserveRoutePaths: true);
        var existing = await _registryReader.GetAppAsync(normalized.AppId, ct);
        if (existing != null &&
            !string.Equals(existing.OwnerScopeId, normalized.OwnerScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"App '{normalized.AppId}' owner scope cannot change from '{existing.OwnerScopeId}' to '{normalized.OwnerScopeId}'.");
        }

        return await _registryWriter.UpsertAppAsync(normalized, ct);
    }

    private static AppDefinitionSnapshot NormalizeDefinition(AppDefinitionSnapshot definition, bool preserveRoutePaths)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var normalized = new AppDefinitionSnapshot
        {
            AppId = NormalizeRequired(definition.AppId, nameof(definition.AppId)),
            OwnerScopeId = NormalizeRequired(definition.OwnerScopeId, nameof(definition.OwnerScopeId)),
            DisplayName = NormalizeOptional(definition.DisplayName),
            Description = NormalizeOptional(definition.Description),
            Visibility = definition.Visibility == AppVisibility.Unspecified ? AppVisibility.Private : definition.Visibility,
            DefaultReleaseId = NormalizeOptional(definition.DefaultReleaseId),
        };

        if (preserveRoutePaths)
            normalized.RoutePaths.Add(definition.RoutePaths.Select(static x => x?.Trim() ?? string.Empty).Where(static x => x.Length > 0));

        return normalized;
    }

    private static AppReleaseSnapshot NormalizeRelease(AppReleaseSnapshot release)
    {
        ArgumentNullException.ThrowIfNull(release);

        var normalized = new AppReleaseSnapshot
        {
            AppId = NormalizeRequired(release.AppId, nameof(release.AppId)),
            ReleaseId = NormalizeRequired(release.ReleaseId, nameof(release.ReleaseId)),
            DisplayName = NormalizeOptional(release.DisplayName),
            Status = release.Status == AppReleaseStatus.Unspecified ? AppReleaseStatus.Draft : release.Status,
        };

        var services = release.ServiceRefs.Select(NormalizeService).ToArray();
        EnsureUniqueServiceIds(services, normalized.AppId, normalized.ReleaseId);
        normalized.ServiceRefs.Add(services);

        var entries = release.EntryRefs
            .Select(x => new AppEntryRef
            {
                EntryId = NormalizeRequired(x.EntryId, nameof(x.EntryId)),
                ServiceId = NormalizeRequired(x.ServiceId, nameof(x.ServiceId)),
                EndpointId = NormalizeRequired(x.EndpointId, nameof(x.EndpointId)),
            })
            .ToArray();
        EnsureValidEntries(entries, services, normalized.AppId, normalized.ReleaseId);
        normalized.EntryRefs.Add(entries);

        var connectors = release.ConnectorRefs.Select(NormalizeConnector).ToArray();
        EnsureUniqueConnectorResourceIds(connectors, normalized.AppId, normalized.ReleaseId);
        normalized.ConnectorRefs.Add(connectors);

        var secrets = release.SecretRefs.Select(NormalizeSecret).ToArray();
        EnsureUniqueSecretResourceIds(secrets, normalized.AppId, normalized.ReleaseId);
        normalized.SecretRefs.Add(secrets);

        return normalized;
    }

    private static AppServiceRef NormalizeService(AppServiceRef service)
    {
        ArgumentNullException.ThrowIfNull(service);

        return new AppServiceRef
        {
            TenantId = NormalizeOptional(service.TenantId),
            AppId = NormalizeOptional(service.AppId),
            Namespace = NormalizeOptional(service.Namespace),
            ServiceId = NormalizeRequired(service.ServiceId, nameof(service.ServiceId)),
            RevisionId = NormalizeOptional(service.RevisionId),
            ImplementationKind = service.ImplementationKind,
            Role = service.Role == AppServiceRole.Unspecified ? AppServiceRole.Internal : service.Role,
        };
    }

    private static AppConnectorRef NormalizeConnector(AppConnectorRef connector)
    {
        ArgumentNullException.ThrowIfNull(connector);

        return new AppConnectorRef
        {
            ResourceId = NormalizeRequired(connector.ResourceId, nameof(connector.ResourceId)),
            ConnectorName = NormalizeRequired(connector.ConnectorName, nameof(connector.ConnectorName)),
        };
    }

    private static AppSecretRef NormalizeSecret(AppSecretRef secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        return new AppSecretRef
        {
            ResourceId = NormalizeRequired(secret.ResourceId, nameof(secret.ResourceId)),
            SecretName = NormalizeRequired(secret.SecretName, nameof(secret.SecretName)),
        };
    }

    private static void EnsureUniqueServiceIds(
        IReadOnlyList<AppServiceRef> services,
        string appId,
        string releaseId)
    {
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            if (!serviceIds.Add(service.ServiceId))
            {
                throw new InvalidOperationException(
                    $"Duplicate service '{service.ServiceId}' for app '{appId}' release '{releaseId}'.");
            }
        }
    }

    private static void EnsureValidEntries(
        IReadOnlyList<AppEntryRef> entries,
        IReadOnlyList<AppServiceRef> services,
        string appId,
        string releaseId)
    {
        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!entryIds.Add(entry.EntryId))
            {
                throw new InvalidOperationException(
                    $"Duplicate function '{entry.EntryId}' for app '{appId}' release '{releaseId}'.");
            }

            var service = services.FirstOrDefault(x => string.Equals(x.ServiceId, entry.ServiceId, StringComparison.Ordinal));
            if (service == null)
            {
                throw new InvalidOperationException(
                    $"Function '{entry.EntryId}' in app '{appId}' release '{releaseId}' points to unknown service '{entry.ServiceId}'.");
            }

            if (service.Role != AppServiceRole.Entry)
            {
                throw new InvalidOperationException(
                    $"Function '{entry.EntryId}' in app '{appId}' release '{releaseId}' points to service '{entry.ServiceId}' that is not marked as entry.");
            }
        }
    }

    private static void EnsureUniqueConnectorResourceIds(
        IEnumerable<AppConnectorRef> connectors,
        string appId,
        string releaseId)
    {
        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connector in connectors)
        {
            if (!resourceIds.Add(connector.ResourceId))
            {
                throw new InvalidOperationException(
                    $"Duplicate connector resource '{connector.ResourceId}' for app '{appId}' release '{releaseId}'.");
            }
        }
    }

    private static void EnsureUniqueSecretResourceIds(
        IEnumerable<AppSecretRef> secrets,
        string appId,
        string releaseId)
    {
        var resourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var secret in secrets)
        {
            if (!resourceIds.Add(secret.ResourceId))
            {
                throw new InvalidOperationException(
                    $"Duplicate secret resource '{secret.ResourceId}' for app '{appId}' release '{releaseId}'.");
            }
        }
    }

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{paramName} is required.");

        return normalized;
    }

    private static string NormalizeOptional(string? value) => value?.Trim() ?? string.Empty;
}
