using Google.Protobuf;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Factory for creating per-agent event sourcing behavior instances.
/// </summary>
public interface IEventSourcingBehaviorFactory<TState>
    where TState : class, IMessage<TState>, new()
{
    /// <summary>
    /// Creates event sourcing behavior for the specified agent and transition function.
    /// </summary>
    IEventSourcingBehavior<TState> Create(
        string agentId,
        Func<TState, IMessage, TState> transitionState);
}
