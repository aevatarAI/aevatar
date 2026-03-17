using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceCommandTargetProvisioner
{
    Task<string> EnsureDefinitionTargetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<string> EnsureRevisionCatalogTargetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<string> EnsureDeploymentTargetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<string> EnsureServingSetTargetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<string> EnsureRolloutTargetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
