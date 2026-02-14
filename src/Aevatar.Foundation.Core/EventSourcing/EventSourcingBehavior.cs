// ─────────────────────────────────────────────────────────────
// EventSourcingBehavior — ES mixin default implementation.
// Provides RaiseEvent / ConfirmEventsAsync / ReplayAsync.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Default implementation of Event Sourcing behavior.
/// </summary>
public class EventSourcingBehavior<TState> : IEventSourcingBehavior<TState>
    where TState : class, IMessage<TState>, new()
{
    private readonly IEventStore _eventStore;
    private readonly List<IMessage> _pending = [];
    private readonly string _agentId;
    private long _currentVersion;

    public EventSourcingBehavior(IEventStore eventStore, string agentId)
    {
        _eventStore = eventStore;
        _agentId = agentId;
    }

    /// <inheritdoc />
    public long CurrentVersion => _currentVersion;

    /// <inheritdoc />
    public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage =>
        _pending.Add(evt);

    /// <inheritdoc />
    public async Task ConfirmEventsAsync(CancellationToken ct = default)
    {
        if (_pending.Count == 0) return;

        var stateEvents = _pending.Select((evt, i) => new StateEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Timestamp = TimestampHelper.Now(),
            Version = _currentVersion + i + 1,
            EventType = evt.Descriptor.FullName,
            EventData = Any.Pack(evt),
            AgentId = _agentId,
        });

        _currentVersion = await _eventStore.AppendAsync(
            _agentId, stateEvents, _currentVersion, ct);
        _pending.Clear();
    }

    /// <inheritdoc />
    public async Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default)
    {
        var events = await _eventStore.GetEventsAsync(agentId, fromVersion: null, ct);
        if (events.Count == 0) return null;

        var state = new TState();
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
    public virtual TState TransitionState(TState current, IMessage evt) => current;
}
