using Google.Protobuf;
namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Typed base class for state event appliers.
/// Supports both raw TEvent and Any-packed TEvent payloads.
/// </summary>
public abstract class StateEventApplierBase<TState, TEvent>
    : IStateEventApplier<TState>
    where TState : class, IMessage<TState>, new()
    where TEvent : class, IMessage<TEvent>, new()
{
    public virtual int Order => 0;

    public bool TryApply(TState current, IMessage evt, out TState next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(evt);

        if (StateTransitionMatcher.TryExtract<TEvent>(evt, out var typed))
        {
            next = Apply(current, typed);
            return true;
        }

        next = current;
        return false;
    }

    protected abstract TState Apply(TState current, TEvent evt);
}
