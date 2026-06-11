using Aevatar.Foundation.Abstractions.EventSourcing;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Provides projection scope activation plans for one committed state publication.
/// </summary>
// Refactor (iter18/cluster-006):
//   Old pattern: command-path projection activation facade with new actor/lifecycle phase
//   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
public interface IProjectionActivationPlanProvider
{
    IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context);
}
