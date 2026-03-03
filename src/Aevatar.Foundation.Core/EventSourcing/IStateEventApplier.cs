using Google.Protobuf;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Stateful event applier abstraction for replay and runtime state transitions.
/// </summary>
public interface IStateEventApplier<TState>
    where TState : class, IMessage<TState>, new()
{
    /// <summary>
    /// Execution order for multiple appliers; lower value executes first.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Tries to apply one event to current state.
    /// Returns true when handled and provides the next state.
    /// </summary>
    bool TryApply(TState current, IMessage evt, out TState next);
}
