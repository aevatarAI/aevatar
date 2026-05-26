using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Helpers for the stable schedule state carried by schedulable channel actors.
/// </summary>
public sealed partial class ScheduleState
{
    /// <summary>
    /// Gets or sets the next scheduled execution timestamp in UTC.
    /// </summary>
    public DateTimeOffset? NextRunAtUtc
    {
        get => NextRunAt == null ? null : NextRunAt.ToDateTimeOffset();
        set => NextRunAt = value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;
    }

    /// <summary>
    /// Gets or sets the last completed execution timestamp in UTC.
    /// </summary>
    public DateTimeOffset? LastRunAtUtc
    {
        get => LastRunAt == null ? null : LastRunAt.ToDateTimeOffset();
        set => LastRunAt = value.HasValue ? Timestamp.FromDateTimeOffset(value.Value.ToUniversalTime()) : null;
    }
}
