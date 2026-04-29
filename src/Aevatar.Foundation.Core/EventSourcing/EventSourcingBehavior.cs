// ─────────────────────────────────────────────────────────────
// EventSourcingBehavior — explicit event-first default implementation.
// Provides RaiseEvent / ConfirmEventsAsync / PersistSnapshotAsync / ReplayAsync.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Default implementation of Event Sourcing behavior.
/// </summary>
public class EventSourcingBehavior<TState> : IEventSourcingBehavior<TState>
    where TState : class, IMessage<TState>, new()
{
    private readonly IEventStore _eventStore;
    private readonly IEventSourcingSnapshotStore<TState>? _snapshotStore;
    private readonly ISnapshotStrategy _snapshotStrategy;
    private readonly IEventStoreCompactionScheduler? _compactionScheduler;
    private readonly bool _enableEventCompaction;
    private readonly int _retainedEventsAfterSnapshot;
    private readonly bool _recoverFromVersionDriftOnReplay;
    private readonly ILogger<EventSourcingBehavior<TState>> _logger;
    private readonly List<IMessage> _pending = [];
    private readonly string _agentId;
    private long _currentVersion;

    public EventSourcingBehavior(
        IEventStore eventStore,
        string agentId,
        IEventSourcingSnapshotStore<TState>? snapshotStore = null,
        ISnapshotStrategy? snapshotStrategy = null,
        ILogger<EventSourcingBehavior<TState>>? logger = null,
        bool enableEventCompaction = false,
        int retainedEventsAfterSnapshot = 0,
        IEventStoreCompactionScheduler? compactionScheduler = null,
        bool recoverFromVersionDriftOnReplay = false)
    {
        _eventStore = eventStore;
        _agentId = agentId;
        _snapshotStore = snapshotStore;
        _snapshotStrategy = snapshotStrategy ?? NeverSnapshotStrategy.Instance;
        _compactionScheduler = compactionScheduler;
        _enableEventCompaction = enableEventCompaction;
        _retainedEventsAfterSnapshot = Math.Max(0, retainedEventsAfterSnapshot);
        _recoverFromVersionDriftOnReplay = recoverFromVersionDriftOnReplay;
        _logger = logger ?? NullLogger<EventSourcingBehavior<TState>>.Instance;
    }

    /// <inheritdoc />
    public long CurrentVersion => _currentVersion;

    /// <inheritdoc />
    public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage =>
        _pending.Add(evt);

    /// <inheritdoc />
    public async Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
    {
        var pendingEvents = _pending.ToArray();
        if (pendingEvents.Length == 0)
        {
            return new EventStoreCommitResult
            {
                AgentId = _agentId,
                LatestVersion = _currentVersion,
            };
        }

        EnsureNoStateSnapshotEvents(pendingEvents);

        var fromVersion = _currentVersion;
        var eventType = JoinEventTypes(pendingEvents);
        var startedAt = Stopwatch.GetTimestamp();
        var stateEvents = pendingEvents.Select((evt, i) => new StateEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Timestamp = TimestampHelper.Now(),
            Version = _currentVersion + i + 1,
            EventType = evt.Descriptor.FullName,
            EventData = Any.Pack(evt),
            AgentId = _agentId,
        }).ToArray();

        try
        {
            var commitResult = await _eventStore.AppendAsync(
                _agentId, stateEvents, _currentVersion, ct);
            _currentVersion = commitResult.LatestVersion;
            RemoveCommittedPendingPrefix(pendingEvents.Length);
            _logger.LogInformation(
                "Event sourcing commit completed. agentId={AgentId} eventType={EventType} version={Version} elapsedMs={ElapsedMs} result={Result}",
                _agentId,
                eventType,
                _currentVersion,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                "ok");
            return commitResult;
        }
        catch (EventStoreOptimisticConcurrencyException ex)
        {
            // In-memory _currentVersion is behind the store's authoritative
            // version. Without refreshing, every retry would rebuild the same
            // stateEvents at the same expectedVersion and conflict again,
            // wedging the actor until it deactivates. Update to the store's
            // version so the next ConfirmEventsAsync (typically driven by the
            // runtime envelope retry policy) recomputes versions and commits
            // cleanly.
            //
            // Drop the prefix that was supposed to commit in this batch from
            // _pending. The runtime envelope retry replays the same envelope,
            // which re-executes the handler — the handler will call
            // RaiseEvent again for the same logical events, so leaving the
            // committed-prefix in _pending would duplicate them on the next
            // commit. The suffix raised mid-flight (existing
            // ConfirmEventsAsync_WhenNewEventIsRaisedDuringAppend contract)
            // is preserved.
            //
            // This trims based on the assumption that the store guarantees
            // full-batch atomicity on conflict (Garnet's AppendScript Lua
            // transaction; InMemoryEventStore's lock-and-check) — no events
            // from this batch made it to the store, and the entire prefix
            // will be re-raised by the handler on retry. A future store with
            // partial-commit semantics would need to compute the actual
            // committed-prefix length (e.g. ex.ActualVersion - fromVersion)
            // and trim only that many entries.
            //
            // Guard against a malformed exception that reports an
            // ActualVersion behind the in-memory _currentVersion: silently
            // accepting it would rewind the actor and likely corrupt the
            // event sequence on the next commit. Keep _currentVersion as the
            // higher of the two values and log loudly so the underlying
            // contract violation surfaces.
            var refreshed = Math.Max(_currentVersion, ex.ActualVersion);
            if (refreshed != ex.ActualVersion)
            {
                _logger.LogError(
                    ex,
                    "Event sourcing commit hit optimistic concurrency conflict reporting an ActualVersion below in-memory _currentVersion. Keeping the larger value to avoid rewinding state. agentId={AgentId} eventType={EventType} expectedVersion={ExpectedVersion} actualVersion={ActualVersion} currentVersion={CurrentVersion}",
                    _agentId,
                    eventType,
                    ex.ExpectedVersion,
                    ex.ActualVersion,
                    _currentVersion);
            }
            else
            {
                _logger.LogWarning(
                    ex,
                    "Event sourcing commit hit optimistic concurrency conflict; refreshing _currentVersion and dropping the rejected batch from _pending so handler re-execution can replay it. agentId={AgentId} eventType={EventType} expectedVersion={ExpectedVersion} actualVersion={ActualVersion} droppedFromPending={DroppedFromPending} elapsedMs={ElapsedMs}",
                    _agentId,
                    eventType,
                    ex.ExpectedVersion,
                    ex.ActualVersion,
                    pendingEvents.Length,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }
            _currentVersion = refreshed;
            RemoveCommittedPendingPrefix(pendingEvents.Length);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Event sourcing commit failed. agentId={AgentId} eventType={EventType} version={Version} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
                _agentId,
                eventType,
                fromVersion,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                "failed",
                ex.GetType().Name);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task PersistSnapshotAsync(
        TState currentState,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ct.ThrowIfCancellationRequested();

        if (_snapshotStore == null)
            return;

        if (!_snapshotStrategy.ShouldCreateSnapshot(_currentVersion))
            return;

        try
        {
            await _snapshotStore.SaveAsync(
                _agentId,
                new EventSourcingSnapshot<TState>(currentState.Clone(), _currentVersion),
                ct);
            await TryScheduleCompactionAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Event sourcing snapshot save failed and will be ignored. agentId={AgentId} version={Version} result={Result} errorType={ErrorType}",
                _agentId,
                _currentVersion,
                "ignored",
                ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public async Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default)
    {
        var snapshot = await TryLoadSnapshotAsync(agentId, ct);
        long? fromVersion = snapshot?.Version;
        var events = await _eventStore.GetEventsAsync(agentId, fromVersion, ct);

        // Always probe the store's authoritative version key. Partial
        // compaction can leave the events sorted set with valid (but
        // trailing) entries while the version key is ahead —
        // events[^1].Version < storeVersion is a real shape, not just the
        // empty-events case. Skipping the probe in the events.Count > 0
        // branch would miss it. The cost is one extra store read per
        // activation; for Garnet that's a single Redis GET co-located with
        // the events query, which is negligible relative to the actor
        // activation envelope.
        var storeVersion = await _eventStore.GetVersionAsync(agentId, ct);

        var state = snapshot?.State ?? new TState();
        foreach (var stateEvent in events)
        {
            if (stateEvent.EventData != null)
                state = TransitionState(state, stateEvent.EventData);
        }

        var replayedVersion = events.Count > 0
            ? events[^1].Version
            : (snapshot?.Version ?? 0);

        if (storeVersion > replayedVersion)
        {
            // Drift: trailing committed events are missing from the events
            // sequence (interrupted Lua append, partial compaction that
            // wiped events but left the version key, externally-seeded
            // store, etc.). Activating at replayedVersion would
            // permanently conflict on every AppendAsync; activating at
            // storeVersion would silently build new authoritative state on
            // top of facts that were never applied. Default to throwing so
            // the operator decides; only recover automatically when the
            // host has opted into RecoverFromVersionDriftOnReplay (see
            // EventSourcingRuntimeOptions for the safety contract).
            if (!_recoverFromVersionDriftOnReplay)
            {
                _logger.LogError(
                    "Event sourcing replay detected version drift and recovery is disabled. agentId={AgentId} replayedVersion={ReplayedVersion} storeVersion={StoreVersion} eventsCount={EventsCount} hasSnapshot={HasSnapshot}",
                    agentId,
                    replayedVersion,
                    storeVersion,
                    events.Count,
                    snapshot != null);
                throw new EventStoreVersionDriftException(agentId, replayedVersion, storeVersion);
            }

            _logger.LogWarning(
                "Event sourcing replay recovering from version drift; activating at the store version with stale state. agentId={AgentId} replayedVersion={ReplayedVersion} storeVersion={StoreVersion} eventsCount={EventsCount} hasSnapshot={HasSnapshot}",
                agentId,
                replayedVersion,
                storeVersion,
                events.Count,
                snapshot != null);
            _currentVersion = storeVersion;
            return events.Count == 0 && snapshot == null ? null : state;
        }

        _currentVersion = replayedVersion;
        return events.Count == 0 && snapshot == null ? null : state;
    }

    /// <summary>
    /// Default: returns current unchanged. Override in derived behavior or agent to apply events.
    /// </summary>
    public virtual TState TransitionState(TState current, IMessage evt)
        => current;

    private void EnsureNoStateSnapshotEvents(IReadOnlyList<IMessage> pendingEvents)
    {
        var stateTypeFullName = new TState().Descriptor.FullName;
        if (pendingEvents.Any(evt =>
                string.Equals(evt.Descriptor.FullName, stateTypeFullName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Persisting state snapshot events is forbidden for state '{typeof(TState).FullName}'. " +
                "Emit domain events instead.");
        }
    }

    private void RemoveCommittedPendingPrefix(int committedCount)
    {
        if (committedCount <= 0)
            return;

        if (_pending.Count <= committedCount)
        {
            _pending.Clear();
            return;
        }

        _pending.RemoveRange(0, committedCount);
    }

    private async Task<EventSourcingSnapshot<TState>?> TryLoadSnapshotAsync(
        string agentId,
        CancellationToken ct)
    {
        if (_snapshotStore == null)
            return null;

        var snapshot = await _snapshotStore.LoadAsync(agentId, ct);
        if (snapshot == null)
            return null;

        return new EventSourcingSnapshot<TState>(snapshot.State.Clone(), snapshot.Version);
    }

    private static string JoinEventTypes(IEnumerable<IMessage> events)
    {
        var eventTypes = events
            .Select(evt => evt.Descriptor.FullName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return eventTypes.Length == 0 ? "<none>" : string.Join(",", eventTypes);
    }

    private async Task TryScheduleCompactionAsync(CancellationToken ct)
    {
        if (!_enableEventCompaction)
            return;

        if (_compactionScheduler == null)
            return;

        var compactToVersion = _currentVersion - _retainedEventsAfterSnapshot;
        if (compactToVersion <= 0)
            return;

        try
        {
            await _compactionScheduler.ScheduleAsync(_agentId, compactToVersion, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Event sourcing compaction scheduling failed and will be ignored. agentId={AgentId} compactToVersion={CompactToVersion} retainedRecentEvents={RetainedRecentEvents} result={Result} errorType={ErrorType}",
                _agentId,
                compactToVersion,
                _retainedEventsAfterSnapshot,
                "ignored",
                ex.GetType().Name);
        }
    }
}
