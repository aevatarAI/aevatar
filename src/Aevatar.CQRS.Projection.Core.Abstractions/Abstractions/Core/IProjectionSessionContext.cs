namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Shared durable materialization scope for one actor-scoped projection pipeline.
/// </summary>
public interface IProjectionMaterializationContext
{
    string RootActorId { get; }

    string ProjectionKind { get; }
}

/// <summary>
/// Runtime session scope for one externally observable projection stream subscription.
/// </summary>
public interface IProjectionSessionContext : IProjectionMaterializationContext
{
    string SessionId { get; }
}
