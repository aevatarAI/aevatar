namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Unified start request for opening a projection scope (session or materialization).
/// </summary>
public sealed record ProjectionScopeStartRequest
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }

    public required ProjectionRuntimeMode Mode { get; init; }

    public string SessionId { get; init; } = string.Empty;
}
