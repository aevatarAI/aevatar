using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.GAgents;

namespace Aevatar.GAgentService.Infrastructure.Activation;

public sealed class DefaultServiceCommandTargetProvisioner : ActorTargetProvisionerBase, IServiceCommandTargetProvisioner
{
    public DefaultServiceCommandTargetProvisioner(IActorRuntime runtime)
        : base(runtime)
    {
    }

    public Task<string> EnsureDefinitionTargetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
        EnsureActorAsync<ServiceDefinitionGAgent>(ServiceActorIds.Definition(identity), ct);

    public Task<string> EnsureRevisionCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
        EnsureActorAsync<ServiceRevisionCatalogGAgent>(ServiceActorIds.RevisionCatalog(identity), ct);

    public Task<string> EnsureDeploymentTargetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
        EnsureActorAsync<ServiceDeploymentManagerGAgent>(ServiceActorIds.Deployment(identity), ct);
}
