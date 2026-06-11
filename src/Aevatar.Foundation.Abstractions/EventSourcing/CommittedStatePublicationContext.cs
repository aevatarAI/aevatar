using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Abstractions.EventSourcing;

/// <summary>
/// Context for one committed state event observer publication.
/// </summary>
public sealed record CommittedStatePublicationContext
{
    public required string ActorId { get; init; }

    public required Type ActorType { get; init; }

    public required CommittedStateEventPublished Published { get; init; }

    public EventEnvelope? SourceEnvelope { get; init; }

    public ObserverAudience Audience { get; init; } = ObserverAudience.CommittedFacts;
}
