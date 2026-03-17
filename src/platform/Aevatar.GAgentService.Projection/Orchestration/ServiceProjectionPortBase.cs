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
        IProjectionMaterializationActivationService<ServiceProjectionRuntimeLease<TContext>> activationService,
        IProjectionMaterializationReleaseService<ServiceProjectionRuntimeLease<TContext>> releaseService,
        string projectionName)
        : base(
            () => options?.Enabled ?? false,
            activationService,
            releaseService)
    {
        ArgumentNullException.ThrowIfNull(options);
        _projectionName = projectionName ?? throw new ArgumentNullException(nameof(projectionName));
    }

    protected async Task EnsureProjectionCoreAsync(string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await EnsureProjectionAsync(
            new ProjectionMaterializationStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = _projectionName,
            },
            ct);
    }
}
