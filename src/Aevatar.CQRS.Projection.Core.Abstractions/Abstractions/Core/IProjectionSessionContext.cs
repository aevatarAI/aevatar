namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Runtime session scope for one externally observable projection stream subscription.
/// </summary>
public interface IProjectionSessionContext : IProjectionMaterializationContext
{
    string SessionId { get; }
}
