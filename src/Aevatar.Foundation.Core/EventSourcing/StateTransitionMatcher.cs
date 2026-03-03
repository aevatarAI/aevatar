using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Helper for deterministic event-to-state transitions.
/// Supports both raw event instances and Any-packed payloads.
/// </summary>
public static class StateTransitionMatcher
{
    public static StateTransitionBuilder<TState> Match<TState>(TState current, IMessage evt)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(evt);
        return new StateTransitionBuilder<TState>(current, evt);
    }

    public static bool TryExtract<TEvent>(IMessage evt, out TEvent extracted)
        where TEvent : class, IMessage<TEvent>, new()
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (evt is TEvent typed)
        {
            extracted = typed;
            return true;
        }

        if (evt is Any any && any.TryUnpack<TEvent>(out var unpacked))
        {
            extracted = unpacked;
            return true;
        }

        extracted = null!;
        return false;
    }
}

public sealed class StateTransitionBuilder<TState>
{
    private readonly TState _current;
    private readonly IMessage _evt;
    private bool _matched;
    private TState _next;

    internal StateTransitionBuilder(TState current, IMessage evt)
    {
        _current = current;
        _evt = evt;
        _next = current;
    }

    public StateTransitionBuilder<TState> On<TEvent>(Func<TState, TEvent, TState> applier)
        where TEvent : class, IMessage<TEvent>, new()
    {
        ArgumentNullException.ThrowIfNull(applier);

        if (_matched)
            return this;

        if (!StateTransitionMatcher.TryExtract<TEvent>(_evt, out var typedEvent))
            return this;

        _next = applier(_current, typedEvent);
        _matched = true;
        return this;
    }

    public TState OrCurrent() => _matched ? _next : _current;
}
