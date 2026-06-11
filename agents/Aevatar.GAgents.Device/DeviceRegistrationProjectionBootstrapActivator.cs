using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.Device;

internal sealed class DeviceRegistrationProjectionBootstrapActivator
{
    public const string ProjectionKind = "device-registration";

    private readonly IProjectionScopeActivationService<DeviceRegistrationMaterializationRuntimeLease> _activationService;

    public DeviceRegistrationProjectionBootstrapActivator(
        IProjectionScopeActivationService<DeviceRegistrationMaterializationRuntimeLease> activationService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    // Refactor (iter52/issue-905-public-projection-ensure-ports):
    //   Old pattern: Public application/agent projection ports exposed actorId-based EnsureProjection/EnsureActorProjection as general callable surface.
    //   New principle: Projection activation is owned by projection bootstrap/lease/session contracts (bootstrap-internal); public application/query ports only support Attach*/Release*/Query* on existing leases.
    public Task<DeviceRegistrationMaterializationRuntimeLease> ActivateWellKnownRegistryAsync(
        CancellationToken ct = default) =>
        _activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = DeviceRegistrationGAgent.WellKnownId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
}
