using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowTimestampCodec
{
    public static Timestamp ToTimestamp(DateTimeOffset value) =>
        Timestamp.FromDateTime(value.UtcDateTime);

    public static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value == null
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(DateTime.SpecifyKind(value.ToDateTime(), DateTimeKind.Utc));
}
