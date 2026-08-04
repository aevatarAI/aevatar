using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>Surfaces a committed fact that could not complete publication or checkpointing.</summary>
public sealed class CommittedStatePublicationException : Exception
{
    public CommittedStatePublicationException(
        string actorId,
        StateEvent stateEvent,
        CommittedStatePublicationFailureStage stage,
        Exception innerException)
        : base(
            $"Committed-state publication {stage} failed for actor '{actorId}', " +
            $"version {stateEvent.Version}, event '{stateEvent.EventId}'.",
            innerException)
    {
        ActorId = actorId;
        Version = stateEvent.Version;
        EventId = stateEvent.EventId;
        Stage = stage;
    }

    public string ActorId { get; }

    public long Version { get; }

    public string EventId { get; }

    public CommittedStatePublicationFailureStage Stage { get; }
}
