using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class AppFunctionQueryApplicationService : IAppFunctionQueryPort
{
    private readonly IAppRegistryReader _registryReader;
    private readonly IServiceLifecycleQueryPort? _serviceLifecycleQueryPort;

    public AppFunctionQueryApplicationService(
        IAppRegistryReader registryReader,
        IServiceLifecycleQueryPort? serviceLifecycleQueryPort = null)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort;
    }

    public async Task<IReadOnlyList<AppFunctionDescriptor>> ListAsync(
        string appId,
        string? releaseId = null,
        CancellationToken ct = default)
    {
        var release = await ResolveReleaseAsync(appId, releaseId, ct);
        if (release == null)
            return [];

        var functions = new List<AppFunctionDescriptor>(release.EntryRefs.Count);
        foreach (var entry in release.EntryRefs.OrderBy(x => x.EntryId, StringComparer.OrdinalIgnoreCase))
        {
            var descriptor = await BuildDescriptorAsync(release, entry, ct);
            if (descriptor != null)
                functions.Add(descriptor);
        }

        return functions;
    }

    public async Task<AppFunctionDescriptor?> GetAsync(
        string appId,
        string functionId,
        string? releaseId = null,
        CancellationToken ct = default)
    {
        var release = await ResolveReleaseAsync(appId, releaseId, ct);
        if (release == null)
            return null;

        var normalizedFunctionId = NormalizeRequired(functionId, nameof(functionId));
        var entry = release.EntryRefs.FirstOrDefault(x =>
            string.Equals(x.EntryId, normalizedFunctionId, StringComparison.Ordinal));
        return entry == null ? null : await BuildDescriptorAsync(release, entry, ct);
    }

    private async Task<AppFunctionDescriptor?> BuildDescriptorAsync(
        AppReleaseSnapshot release,
        AppEntryRef entry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(entry);

        var serviceRef = release.ServiceRefs.FirstOrDefault(x =>
            string.Equals(x.ServiceId, entry.ServiceId, StringComparison.Ordinal));

        var endpoint = serviceRef == null
            ? null
            : await ResolveEndpointAsync(serviceRef, entry.EndpointId, ct);

        return new AppFunctionDescriptor
        {
            FunctionId = entry.EntryId ?? string.Empty,
            DisplayName = ResolveDisplayName(entry, endpoint),
            Description = endpoint?.Description ?? string.Empty,
            AppId = release.AppId ?? string.Empty,
            ReleaseId = release.ReleaseId ?? string.Empty,
            ServiceId = entry.ServiceId ?? string.Empty,
            EndpointId = entry.EndpointId ?? string.Empty,
            EndpointKind = ResolveEndpointKind(endpoint?.Kind),
            RequestTypeUrl = endpoint?.RequestTypeUrl ?? string.Empty,
            ResponseTypeUrl = endpoint?.ResponseTypeUrl ?? string.Empty,
        };
    }

    private async Task<ServiceEndpointSnapshot?> ResolveEndpointAsync(
        AppServiceRef serviceRef,
        string endpointId,
        CancellationToken ct)
    {
        if (_serviceLifecycleQueryPort == null)
            return null;

        var identity = new ServiceIdentity
        {
            TenantId = serviceRef.TenantId ?? string.Empty,
            AppId = serviceRef.AppId ?? string.Empty,
            Namespace = serviceRef.Namespace ?? string.Empty,
            ServiceId = serviceRef.ServiceId ?? string.Empty,
        };

        var revisions = await _serviceLifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
        var revisionEndpoint = revisions?.Revisions
            .FirstOrDefault(x => string.Equals(x.RevisionId, serviceRef.RevisionId, StringComparison.Ordinal))
            ?.Endpoints
            .FirstOrDefault(x => string.Equals(x.EndpointId, endpointId, StringComparison.Ordinal));
        if (revisionEndpoint != null)
            return revisionEndpoint;

        var service = await _serviceLifecycleQueryPort.GetServiceAsync(identity, ct);
        return service?.Endpoints.FirstOrDefault(x =>
            string.Equals(x.EndpointId, endpointId, StringComparison.Ordinal));
    }

    private async Task<AppReleaseSnapshot?> ResolveReleaseAsync(
        string appId,
        string? releaseId,
        CancellationToken ct)
    {
        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        var resolvedReleaseId = releaseId;

        if (string.IsNullOrWhiteSpace(resolvedReleaseId))
        {
            var app = await _registryReader.GetAppAsync(normalizedAppId, ct);
            if (app == null || string.IsNullOrWhiteSpace(app.DefaultReleaseId))
                return null;

            resolvedReleaseId = app.DefaultReleaseId;
        }

        return await _registryReader.GetReleaseAsync(normalizedAppId, resolvedReleaseId.Trim(), ct);
    }

    private static string ResolveDisplayName(AppEntryRef entry, ServiceEndpointSnapshot? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint?.DisplayName))
            return endpoint.DisplayName;

        return entry.EntryId ?? string.Empty;
    }

    private static AppFunctionEndpointKind ResolveEndpointKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "command" => AppFunctionEndpointKind.Command,
            "chat" => AppFunctionEndpointKind.Chat,
            _ => AppFunctionEndpointKind.Unspecified,
        };

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0
            ? throw new ArgumentException($"{name} is required.", name)
            : normalized;
    }
}
