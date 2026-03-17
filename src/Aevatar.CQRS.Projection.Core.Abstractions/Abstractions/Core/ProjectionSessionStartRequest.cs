namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Minimal runtime start contract for opening one projection session.
/// </summary>
public sealed record ProjectionSessionStartRequest
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }

    public required string SessionId { get; init; }
}
