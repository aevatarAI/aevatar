using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Default event sourcing behavior factory.
/// </summary>
public sealed class DefaultEventSourcingBehaviorFactory<TState>
    : IEventSourcingBehaviorFactory<TState>
    where TState : class, IMessage<TState>, new()
{
    private readonly IEventStore _eventStore;

    public DefaultEventSourcingBehaviorFactory(IEventStore eventStore)
    {
        _eventStore = eventStore;
    }

    public IEventSourcingBehavior<TState> Create(
        string agentId,
        Func<TState, IMessage, TState> transitionState)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(transitionState);

        return new DelegatingEventSourcingBehavior(
            _eventStore,
            agentId,
            transitionState);
    }

    private sealed class DelegatingEventSourcingBehavior : EventSourcingBehavior<TState>
    {
        private readonly Func<TState, IMessage, TState> _transitionState;

        public DelegatingEventSourcingBehavior(
            IEventStore eventStore,
            string agentId,
            Func<TState, IMessage, TState> transitionState)
            : base(eventStore, agentId)
        {
            _transitionState = transitionState;
        }

        public override TState TransitionState(TState current, IMessage evt) =>
            _transitionState(current, evt);
    }
}
