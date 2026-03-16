using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

public abstract class ServiceProjectionPortBase<TContext>
    where TContext : class, IProjectionContext
{
    private readonly IProjectionPortActivationService<ServiceProjectionRuntimeLease<TContext>> _activationService;
    private readonly string _projectionName;

    protected ServiceProjectionPortBase(
        IProjectionPortActivationService<ServiceProjectionRuntimeLease<TContext>> activationService,
        string projectionName)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _projectionName = projectionName ?? throw new ArgumentNullException(nameof(projectionName));
    }

    protected async Task EnsureProjectionCoreAsync(string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        _ = await _activationService.EnsureAsync(actorId, _projectionName, string.Empty, actorId, ct);
    }
}
