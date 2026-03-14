namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Template base for projection activation services:
/// acquire(optional) -> create context -> start lifecycle -> post-start(optional) -> runtime lease.
/// </summary>
public abstract class ProjectionActivationServiceBase<TRuntimeLease, TContext, TTopology>
    : IProjectionPortActivationService<TRuntimeLease>
    where TRuntimeLease : class
    where TContext : class, IProjectionContext
{
    private readonly IProjectionLifecycleService<TContext, TTopology> _lifecycle;

    protected ProjectionActivationServiceBase(IProjectionLifecycleService<TContext, TTopology> lifecycle)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public async Task<TRuntimeLease> EnsureAsync(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootEntityId);
        ct.ThrowIfCancellationRequested();

        await AcquireBeforeStartAsync(
            rootEntityId,
            projectionName,
            input,
            commandId,
            ct);

        TContext? context = null;
        TRuntimeLease? runtimeLease = null;
        var started = false;
        try
        {
            context = CreateContext(
                rootEntityId,
                projectionName,
                input,
                commandId,
                ct);
            await _lifecycle.StartAsync(context, ct);
            started = true;
            await OnStartedAsync(rootEntityId, commandId, context, ct);
            runtimeLease = CreateRuntimeLease(context);
            await OnRuntimeLeaseCreatedAsync(
                rootEntityId,
                commandId,
                context,
                runtimeLease,
                ct);
            return runtimeLease;
        }
        catch
        {
            if (started && context != null)
                await TryStopStartedContextAsync(context);

            await CleanupOnStartFailureAsync(rootEntityId, commandId);
            throw;
        }
    }

    protected virtual Task AcquireBeforeStartAsync(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct) => Task.CompletedTask;

    protected virtual Task OnStartedAsync(
        string rootEntityId,
        string commandId,
        TContext context,
        CancellationToken ct) => Task.CompletedTask;

    protected virtual Task OnRuntimeLeaseCreatedAsync(
        string rootEntityId,
        string commandId,
        TContext context,
        TRuntimeLease runtimeLease,
        CancellationToken ct) => Task.CompletedTask;

    protected virtual Task CleanupOnStartFailureAsync(
        string rootEntityId,
        string commandId) => Task.CompletedTask;

    protected abstract TContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct);

    protected abstract TRuntimeLease CreateRuntimeLease(TContext context);

    private async Task TryStopStartedContextAsync(TContext context)
    {
        try
        {
            await _lifecycle.StopAsync(context, CancellationToken.None);
        }
        catch
        {
            // Preserve the original startup failure; ownership cleanup will still run next.
        }
    }
}
