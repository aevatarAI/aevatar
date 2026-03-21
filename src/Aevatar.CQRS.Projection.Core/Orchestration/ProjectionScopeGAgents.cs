namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionMaterializationScopeGAgent<TContext>
    : ProjectionMaterializationScopeGAgentBase<TContext>
    where TContext : class, IProjectionMaterializationContext;

public sealed class ProjectionSessionScopeGAgent<TContext>
    : ProjectionSessionScopeGAgentBase<TContext>
    where TContext : class, IProjectionSessionContext;
