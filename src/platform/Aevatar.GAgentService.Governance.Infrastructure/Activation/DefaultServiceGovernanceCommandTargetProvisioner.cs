using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Activation;

namespace Aevatar.GAgentService.Governance.Infrastructure.Activation;

public sealed class DefaultServiceGovernanceCommandTargetProvisioner
    : ActorTargetProvisionerBase, IServiceGovernanceCommandTargetProvisioner
{
    public DefaultServiceGovernanceCommandTargetProvisioner(IActorRuntime runtime)
        : base(runtime)
    {
    }

    public Task<string> EnsureBindingCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
        EnsureActorAsync<ServiceBindingManagerGAgent>(ServiceActorIds.BindingCatalog(identity), ct);

    public Task<string> EnsureEndpointCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
        EnsureActorAsync<ServiceEndpointCatalogGAgent>(ServiceActorIds.EndpointCatalog(identity), ct);

    public Task<string> EnsurePolicyCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
        EnsureActorAsync<ServicePolicyGAgent>(ServiceActorIds.PolicyCatalog(identity), ct);
}
