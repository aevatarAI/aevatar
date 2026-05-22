using Aevatar.Foundation.Abstractions;
using Aevatar.Interop.A2A.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Interop.A2A.Application;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: task current state lived in IA2ATaskStore process memory.
//   New principle: current-state readmodel is materialized from committed task actor state.
public static class A2ATaskCurrentStateProjector
{
    public static A2ATaskCurrentStateReadModel? TryProject(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload == null || !envelope.Payload.Is(CommittedStateEventPublished.Descriptor))
            return null;

        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published.StateRoot == null || !published.StateRoot.Is(A2ATaskState.Descriptor))
            return null;

        var state = published.StateRoot.Unpack<A2ATaskState>();
        var stateEvent = published.StateEvent;
        if (stateEvent == null)
            return null;

        var actorId = stateEvent.AgentId;
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(state.TaskId))
            return null;

        return new A2ATaskCurrentStateReadModel
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId,
            UpdatedAtUtcValue = stateEvent.Timestamp ?? Timestamp.FromDateTime(DateTime.UtcNow),
            State = state,
        };
    }
}
