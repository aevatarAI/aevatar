namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Generic projection context contract.
/// </summary>
public interface IProjectionContext
{
    string ProjectionId { get; }

    string RootActorId { get; }
}
