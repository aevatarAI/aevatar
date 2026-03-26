using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class AppFunctionExecutionTargetQueryApplicationService : IAppFunctionExecutionTargetQueryPort
{
    private readonly IAppRegistryReader _registryReader;
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;

    public AppFunctionExecutionTargetQueryApplicationService(
        IAppRegistryReader registryReader,
        IServiceLifecycleQueryPort serviceLifecycleQueryPort)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
    }

    public async Task<AppFunctionExecutionTarget?> ResolveAsync(
        string appId,
        string functionId,
        string? releaseId = null,
        CancellationToken ct = default)
    {
        var normalizedAppId = NormalizeRequired(appId, nameof(appId));
        var app = await _registryReader.GetAppAsync(normalizedAppId, ct);
        if (app == null)
            return null;

        var release = await ResolveReleaseAsync(app, releaseId, ct);
        if (release == null)
            return null;

        var normalizedFunctionId = NormalizeRequired(functionId, nameof(functionId));
        var entry = release.EntryRefs.FirstOrDefault(x =>
            string.Equals(x.EntryId, normalizedFunctionId, StringComparison.Ordinal));
        if (entry == null)
            return null;

        var serviceRef = release.ServiceRefs.FirstOrDefault(x =>
            string.Equals(x.ServiceId, entry.ServiceId, StringComparison.Ordinal));
        if (serviceRef == null)
            return null;

        var serviceSnapshot = await _serviceLifecycleQueryPort.GetServiceAsync(
            new ServiceIdentity
            {
                TenantId = serviceRef.TenantId ?? string.Empty,
                AppId = serviceRef.AppId ?? string.Empty,
                Namespace = serviceRef.Namespace ?? string.Empty,
                ServiceId = serviceRef.ServiceId ?? string.Empty,
            },
            ct);

        return new AppFunctionExecutionTarget(
            app,
            release,
            entry,
            serviceRef,
            serviceSnapshot?.PrimaryActorId ?? string.Empty,
            serviceSnapshot?.DeploymentId ?? string.Empty,
            serviceSnapshot?.ActiveServingRevisionId ?? string.Empty);
    }

    private async Task<Aevatar.AppPlatform.Abstractions.AppReleaseSnapshot?> ResolveReleaseAsync(
        Aevatar.AppPlatform.Abstractions.AppDefinitionSnapshot app,
        string? releaseId,
        CancellationToken ct)
    {
        var resolvedReleaseId = releaseId?.Trim();
        if (string.IsNullOrWhiteSpace(resolvedReleaseId))
        {
            if (string.IsNullOrWhiteSpace(app.DefaultReleaseId))
                return null;

            resolvedReleaseId = app.DefaultReleaseId.Trim();
        }

        return await _registryReader.GetReleaseAsync(app.AppId, resolvedReleaseId, ct);
    }

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{paramName} is required.", paramName);

        return normalized;
    }
}
