using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Interop.A2A.Application;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: A2A task projection had no durable materialization scope.
//   New principle: task current-state materialization runs under the Projection Pipeline context contract.
public sealed class A2ATaskProjectionContext : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
