namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceProjectionDescriptor<TContext>
    where TContext : class, IProjectionContext
{
    private readonly Func<string, string, TContext> _contextFactory;
    private readonly Func<TContext, string> _rootActorIdSelector;

    public ServiceProjectionDescriptor(
        Func<string, string, TContext> contextFactory,
        Func<TContext, string> rootActorIdSelector)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _rootActorIdSelector = rootActorIdSelector ?? throw new ArgumentNullException(nameof(rootActorIdSelector));
    }

    public TContext CreateContext(string rootActorId, string projectionName) =>
        _contextFactory(rootActorId, projectionName);

    public string GetRootActorId(TContext context) =>
        _rootActorIdSelector(context);
}
