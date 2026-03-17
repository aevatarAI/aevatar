namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionScopeContextFactory<TContext>
    : IProjectionScopeContextFactory<TContext>
    where TContext : class, IProjectionMaterializationContext
{
    private readonly Func<ProjectionRuntimeScopeKey, TContext> _factory;

    public ProjectionScopeContextFactory(Func<ProjectionRuntimeScopeKey, TContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public TContext Create(ProjectionRuntimeScopeKey scopeKey)
    {
        ArgumentNullException.ThrowIfNull(scopeKey);
        return _factory(scopeKey);
    }
}
