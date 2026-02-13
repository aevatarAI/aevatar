// ─────────────────────────────────────────────────────────────
// SnapshotStrategy — when to create state snapshots (reduce replay cost).
// ─────────────────────────────────────────────────────────────

namespace Aevatar.EventSourcing;

/// <summary>Strategy for when to create snapshots.</summary>
public interface ISnapshotStrategy
{
    /// <summary>Whether a snapshot should be created at the given version.</summary>
    bool ShouldCreateSnapshot(long version);
}

/// <summary>Create a snapshot every N events.</summary>
public sealed class IntervalSnapshotStrategy : ISnapshotStrategy
{
    private readonly int _interval;

    public IntervalSnapshotStrategy(int interval = 100) =>
        _interval = interval > 0 ? interval : 100;

    /// <inheritdoc />
    public bool ShouldCreateSnapshot(long version) =>
        version > 0 && version % _interval == 0;
}

/// <summary>Never create snapshots.</summary>
public sealed class NeverSnapshotStrategy : ISnapshotStrategy
{
    /// <summary>Singleton instance.</summary>
    public static readonly NeverSnapshotStrategy Instance = new();

    /// <inheritdoc />
    public bool ShouldCreateSnapshot(long version) => false;
}
