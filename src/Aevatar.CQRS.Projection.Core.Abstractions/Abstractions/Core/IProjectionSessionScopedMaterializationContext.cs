namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Durable materialization scope that is partitioned by an explicit session key.
/// </summary>
public interface IProjectionSessionScopedMaterializationContext : IProjectionMaterializationContext
{
    string SessionId { get; }
}
