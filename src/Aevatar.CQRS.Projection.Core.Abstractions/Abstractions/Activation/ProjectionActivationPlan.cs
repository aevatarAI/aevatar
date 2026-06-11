namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Activation plan for an existing projection scope.
/// </summary>
// Refactor (iter18/cluster-006):
//   Old pattern: command-path projection activation facade with new actor/lifecycle phase
//   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
public sealed record ProjectionActivationPlan
{
    public required ProjectionScopeStartRequest StartRequest { get; init; }

    public required Type LeaseType { get; init; }
}
