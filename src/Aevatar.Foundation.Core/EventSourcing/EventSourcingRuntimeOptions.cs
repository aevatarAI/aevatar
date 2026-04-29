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

    /// <summary>
    /// When ReplayAsync detects that the store's version key is ahead of the
    /// last applied event/snapshot version (events lost between snapshot and
    /// store version, trailing events lost after compaction, or partial Lua
    /// append), the default behavior is to throw <see
    /// cref="Aevatar.Foundation.Abstractions.Persistence.EventStoreVersionDriftException"/>
    /// and refuse to activate the actor. Silently advancing to the store
    /// version would build new authoritative state on top of facts that were
    /// never applied — for a non-idempotent domain GAgent this is worse than
    /// failing closed because the divergence is invisible to operators.
    ///
    /// Set this flag to <c>true</c> only when every actor sharing this
    /// options instance can tolerate replaying with stale state at the
    /// store-side version. Prefer <see
    /// cref="ShouldRecoverFromVersionDriftOnReplay"/> for per-actor opt-in
    /// (e.g. only projection scope actors) and leave this off as the safe
    /// global default. With recovery active, ReplayAsync logs the drift at
    /// warning, sets <c>_currentVersion</c> to the store version, and lets
    /// the next commit proceed; the ConfirmEventsAsync catch path remains a
    /// second line of defense.
    /// </summary>
    public bool RecoverFromVersionDriftOnReplay { get; set; }

    /// <summary>
    /// Per-agent opt-in predicate evaluated at behavior construction time.
    /// Returning <c>true</c> for a given agent id enables drift recovery on
    /// replay for that actor regardless of the global
    /// <see cref="RecoverFromVersionDriftOnReplay"/> flag. Use this to scope
    /// recovery to actor families that are known to be idempotent (e.g.
    /// <c>agentId =&gt; agentId.StartsWith("projection.durable.scope:")</c>)
    /// without granting the same affordance to domain GAgents that hold
    /// non-idempotent state.
    /// </summary>
    public Func<string, bool>? ShouldRecoverFromVersionDriftOnReplay { get; set; }
}
