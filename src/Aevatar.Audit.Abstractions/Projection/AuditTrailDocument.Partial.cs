using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit;

public sealed partial class AuditTrailDocument
{
    public DateTimeOffset OccurredAtDateTimeOffset
    {
        get => OccurredAt?.ToDateTimeOffset() ?? default;
        set => OccurredAt = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public DateTimeOffset UpdatedAtDateTimeOffset
    {
        get => UpdatedAt?.ToDateTimeOffset() ?? default;
        set => UpdatedAt = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
