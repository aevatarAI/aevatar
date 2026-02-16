namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Generic run-scoped projection context contract.
/// </summary>
public interface IProjectionRunContext
{
    string RunId { get; }

    string RootActorId { get; }
}
