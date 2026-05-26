using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GAgents.Device;

public sealed class DeviceRegistrationMaterializationContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}
