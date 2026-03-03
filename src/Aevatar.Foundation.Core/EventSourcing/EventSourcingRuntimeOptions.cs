namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Runtime defaults for automatic snapshot and event-stream compaction.
/// </summary>
public sealed class EventSourcingRuntimeOptions
{
    /// <summary>
    /// Enables snapshot persistence for stateful agents when a snapshot store is registered.
    /// </summary>
    public bool EnableSnapshots { get; set; } = true;

    /// <summary>
    /// Snapshot interval in committed event versions.
    /// </summary>
    public int SnapshotInterval { get; set; } = 200;

    /// <summary>
    /// Enables deleting historical events after a snapshot is successfully saved.
    /// </summary>
    public bool EnableEventCompaction { get; set; } = true;

    /// <summary>
    /// Number of latest events to retain after compaction.
    /// 0 means delete all events up to snapshot version.
    /// </summary>
    public int RetainedEventsAfterSnapshot { get; set; } = 0;
}
