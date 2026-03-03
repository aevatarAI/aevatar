namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Generic projection context contract.
/// </summary>
public interface IProjectionContext
{
    string ProjectionId { get; }

    string RootActorId { get; }
}
