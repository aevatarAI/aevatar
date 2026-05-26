using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.Device;

public sealed class DeviceRegistrationMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<DeviceRegistrationMaterializationContext>
{
    public DeviceRegistrationMaterializationRuntimeLease(DeviceRegistrationMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public DeviceRegistrationMaterializationContext Context { get; }
}
