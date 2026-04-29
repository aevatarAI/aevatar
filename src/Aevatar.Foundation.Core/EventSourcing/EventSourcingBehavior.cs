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
        IEventStoreCompactionScheduler? compactionScheduler = null)
    {
        _eventStore = eventStore;
        _agentId = agentId;
        _snapshotStore = snapshotStore;
        _snapshotStrategy = snapshotStrategy ?? NeverSnapshotStrategy.Instance;
        _compactionScheduler = compactionScheduler;
        _enableEventCompaction = enableEventCompaction;
        _retainedEventsAfterSnapshot = Math.Max(0, retainedEventsAfterSnapshot);
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
    public void DiscardPendingEvents() => _pending.Clear();

    /// <inheritdoc />
    public async Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default)
    {
        var snapshot = await TryLoadSnapshotAsync(agentId, ct);
        long? fromVersion = snapshot?.Version;
        var events = await _eventStore.GetEventsAsync(agentId, fromVersion, ct);
        if (events.Count == 0)
        {
            if (snapshot == null)
                return null;

            _currentVersion = snapshot.Version;
            return snapshot.State;
        }

        var state = snapshot?.State ?? new TState();
        foreach (var stateEvent in events)
        {
            if (stateEvent.EventData != null)
                state = TransitionState(state, stateEvent.EventData);
        }

        _currentVersion = events[^1].Version;
        return state;
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
