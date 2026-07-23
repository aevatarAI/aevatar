using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Projection.AgentProfiles;

public sealed class AgentProfileNamespaceCurrentStateProjectionContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}

public sealed class AgentProfileOwnerCurrentStateProjectionContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}

public sealed class AgentProfileExecutionCurrentStateProjectionContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}

internal static class AgentProfileProjectionWritePolicy
{
    public static void EnsureAccepted(ProjectionWriteResult result, string documentId)
    {
        if (result.Disposition is ProjectionWriteDisposition.Applied or ProjectionWriteDisposition.Duplicate)
            return;

        throw new InvalidOperationException(
            $"Agent Profile projection write for '{documentId}' was rejected with disposition '{result.Disposition}'.");
    }
}
