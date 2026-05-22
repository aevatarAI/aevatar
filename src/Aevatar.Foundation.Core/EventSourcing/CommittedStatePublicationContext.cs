using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Context for one committed state event observer publication.
/// </summary>
// Refactor (iter18/cluster-006):
//   Old pattern: command-path projection activation facade with new actor/lifecycle phase
//   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
public sealed record CommittedStatePublicationContext
{
    public required string ActorId { get; init; }

    public required Type ActorType { get; init; }

    public required CommittedStateEventPublished Published { get; init; }

    public EventEnvelope? SourceEnvelope { get; init; }

    public ObserverAudience Audience { get; init; } = ObserverAudience.CommittedFacts;
}
