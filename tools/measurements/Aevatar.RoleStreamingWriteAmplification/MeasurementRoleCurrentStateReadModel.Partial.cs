using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.RoleStreamingWriteAmplification;

public sealed partial class MeasurementRoleCurrentStateReadModel
    : IProjectionReadModel<MeasurementRoleCurrentStateReadModel>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue == null ? default : UpdatedAtUtcValue.ToDateTimeOffset();
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
