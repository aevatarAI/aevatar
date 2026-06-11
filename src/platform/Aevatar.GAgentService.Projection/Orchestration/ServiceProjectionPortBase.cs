using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Configuration;

namespace Aevatar.GAgentService.Projection.Orchestration;

public abstract class ServiceProjectionPortBase<TContext>
    : MaterializationProjectionPortBase<ServiceProjectionRuntimeLease<TContext>>
    where TContext : class, IProjectionMaterializationContext
{
    private readonly string _projectionName;

    protected ServiceProjectionPortBase(
        ServiceProjectionOptions options,
        IProjectionScopeReleaseService<ServiceProjectionRuntimeLease<TContext>> releaseService,
        string projectionName)
        : base(
            () => options?.Enabled ?? false,
            releaseService)
    {
        ArgumentNullException.ThrowIfNull(options);
        _projectionName = projectionName ?? throw new ArgumentNullException(nameof(projectionName));
    }

    protected string ProjectionName => _projectionName;
}
