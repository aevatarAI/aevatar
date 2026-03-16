namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ContextProjectionActivationService<TRuntimeLease, TContext, TTopology>
    : IProjectionPortActivationService<TRuntimeLease>
    where TRuntimeLease : class
    where TContext : class, IProjectionContext
{
    private readonly IProjectionLifecycleService<TContext, TTopology> _lifecycle;
    private readonly Func<string, string, string, string, CancellationToken, TContext> _contextFactory;
    private readonly Func<TContext, TRuntimeLease> _runtimeLeaseFactory;
    private readonly Func<string, string, string, string, CancellationToken, Task>? _acquireBeforeStart;
    private readonly Func<string, string, TContext, TRuntimeLease, CancellationToken, Task>? _onRuntimeLeaseCreated;
    private readonly Func<string, string, Task>? _cleanupOnStartFailure;

    public ContextProjectionActivationService(
        IProjectionLifecycleService<TContext, TTopology> lifecycle,
        Func<string, string, string, string, CancellationToken, TContext> contextFactory,
        Func<TContext, TRuntimeLease> runtimeLeaseFactory,
        Func<string, string, string, string, CancellationToken, Task>? acquireBeforeStart = null,
        Func<string, string, TContext, TRuntimeLease, CancellationToken, Task>? onRuntimeLeaseCreated = null,
        Func<string, string, Task>? cleanupOnStartFailure = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _runtimeLeaseFactory = runtimeLeaseFactory ?? throw new ArgumentNullException(nameof(runtimeLeaseFactory));
        _acquireBeforeStart = acquireBeforeStart;
        _onRuntimeLeaseCreated = onRuntimeLeaseCreated;
        _cleanupOnStartFailure = cleanupOnStartFailure;
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

        await (_acquireBeforeStart?.Invoke(rootEntityId, projectionName, input, commandId, ct) ?? Task.CompletedTask);

        TContext? context = null;
        var started = false;
        try
        {
            context = _contextFactory(rootEntityId, projectionName, input, commandId, ct);
            await _lifecycle.StartAsync(context, ct);
            started = true;

            var runtimeLease = _runtimeLeaseFactory(context);
            await (_onRuntimeLeaseCreated?.Invoke(rootEntityId, commandId, context, runtimeLease, ct) ?? Task.CompletedTask);
            return runtimeLease;
        }
        catch
        {
            if (started && context != null)
            {
                try
                {
                    await _lifecycle.StopAsync(context, CancellationToken.None);
                }
                catch
                {
                }
            }

            await (_cleanupOnStartFailure?.Invoke(rootEntityId, commandId) ?? Task.CompletedTask);
            throw;
        }
    }
}
