namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Marks one actor state as carrying the shared schedule contract used by channel-triggered runners.
/// </summary>
public interface ISchedulable
{
    /// <summary>
    /// Gets the stable schedule state owned by this object.
    /// </summary>
    ScheduleState Schedule { get; }
}
