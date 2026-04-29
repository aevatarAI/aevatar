namespace Aevatar.Foundation.Abstractions.Persistence;

/// <summary>
/// Raised by ReplayAsync when the event store's version key is ahead of the
/// last applied event/snapshot version — trailing events are missing from
/// the events sequence, the snapshot lags the store, or the events sorted
/// set was wiped while the version counter survived. Activating the actor
/// in this state would build new authoritative facts on top of an
/// incomplete history. Default behavior is to throw and let an operator
/// decide; opt in via <c>EventSourcingRuntimeOptions.RecoverFromVersionDriftOnReplay</c>
/// when the actor's transitions are idempotent (e.g. projection scopes).
/// </summary>
public sealed class EventStoreVersionDriftException : InvalidOperationException
{
    public EventStoreVersionDriftException(
        string agentId,
        long replayedVersion,
        long storeVersion)
        : base(
            $"Event store version drift detected for agent '{agentId}': replayed up to version {replayedVersion}, store version is {storeVersion}. " +
            $"Activating would skip {storeVersion - replayedVersion} committed event(s). Set EventSourcingRuntimeOptions.RecoverFromVersionDriftOnReplay=true to recover at the store version with stale state, or repair the store.")
    {
        AgentId = agentId ?? string.Empty;
        ReplayedVersion = replayedVersion;
        StoreVersion = storeVersion;
    }

    public string AgentId { get; }

    public long ReplayedVersion { get; }

    public long StoreVersion { get; }
}
