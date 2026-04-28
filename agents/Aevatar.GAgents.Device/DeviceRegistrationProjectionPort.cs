using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.Device;

public sealed class DeviceRegistrationProjectionPort
    : MaterializationProjectionPortBase<DeviceRegistrationMaterializationRuntimeLease>
{
    public const string ProjectionKind = "device-registration";

    public DeviceRegistrationProjectionPort(
        IProjectionScopeActivationService<DeviceRegistrationMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<DeviceRegistrationMaterializationRuntimeLease?> EnsureProjectionForActorAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
}
