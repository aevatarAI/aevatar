using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.Scheduled;

internal sealed class UserAgentCatalogProjectionBootstrapActivator
{
    public const string ProjectionKind = UserAgentCatalogStorageContracts.DurableProjectionKind;

    private readonly IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease> _activationService;

    public UserAgentCatalogProjectionBootstrapActivator(
        IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    // Refactor (iter52/issue-905-public-projection-ensure-ports):
    //   Old pattern: Public application/agent projection ports exposed actorId-based EnsureProjection/EnsureActorProjection as general callable surface.
    //   New principle: Projection activation is owned by projection bootstrap/lease/session contracts (bootstrap-internal); public application/query ports only support Attach*/Release*/Query* on existing leases.
    public Task<UserAgentCatalogMaterializationRuntimeLease> ActivateWellKnownCatalogAsync(
        CancellationToken ct = default) =>
        _activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = UserAgentCatalogGAgent.WellKnownId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
}
