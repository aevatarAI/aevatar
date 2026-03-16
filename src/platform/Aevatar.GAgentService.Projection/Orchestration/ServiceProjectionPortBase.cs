using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public abstract class ServiceProjectionPortBase<TContext>
    : MaterializationProjectionPortBase<ServiceProjectionRuntimeLease<TContext>>
    where TContext : class, IProjectionMaterializationContext
{
    private readonly string _projectionName;

    protected ServiceProjectionPortBase(
        IProjectionMaterializationActivationService<ServiceProjectionRuntimeLease<TContext>> activationService,
        string projectionName)
        : base(static () => true, activationService)
    {
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
