namespace Aevatar.CQRS.Projection.Core.Abstractions;

public interface IProjectionScopeContextFactory<out TContext>
    where TContext : class, IProjectionMaterializationContext
{
    TContext Create(ProjectionRuntimeScopeKey scopeKey);
}
