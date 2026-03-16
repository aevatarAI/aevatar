namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Minimal runtime start contract for opening one durable materialization scope.
/// </summary>
public sealed record ProjectionMaterializationStartRequest
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}
