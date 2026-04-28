using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Device;

public sealed partial class DeviceRegistrationDocument : IProjectionReadModel<DeviceRegistrationDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtc != null ? UpdatedAtUtc.ToDateTimeOffset() : default;
        set => UpdatedAtUtc = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
