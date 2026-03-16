namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ContextProjectionMaterializationActivationService<TRuntimeLease, TContext>
    : IProjectionMaterializationActivationService<TRuntimeLease>
    where TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
    where TContext : class, IProjectionMaterializationContext
{
    private readonly IProjectionMaterializationLifecycleService<TContext, TRuntimeLease> _lifecycle;
    private readonly Func<ProjectionMaterializationStartRequest, CancellationToken, TContext> _contextFactory;
    private readonly Func<TContext, TRuntimeLease> _runtimeLeaseFactory;
    private readonly Func<ProjectionMaterializationStartRequest, CancellationToken, Task>? _acquireBeforeStart;
    private readonly Func<ProjectionMaterializationStartRequest, TContext, TRuntimeLease, CancellationToken, Task>? _onRuntimeLeaseCreated;
    private readonly Func<ProjectionMaterializationStartRequest, Task>? _cleanupOnStartFailure;

    public ContextProjectionMaterializationActivationService(
        IProjectionMaterializationLifecycleService<TContext, TRuntimeLease> lifecycle,
        Func<ProjectionMaterializationStartRequest, CancellationToken, TContext> contextFactory,
        Func<TContext, TRuntimeLease> runtimeLeaseFactory,
        Func<ProjectionMaterializationStartRequest, CancellationToken, Task>? acquireBeforeStart = null,
        Func<ProjectionMaterializationStartRequest, TContext, TRuntimeLease, CancellationToken, Task>? onRuntimeLeaseCreated = null,
        Func<ProjectionMaterializationStartRequest, Task>? cleanupOnStartFailure = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _runtimeLeaseFactory = runtimeLeaseFactory ?? throw new ArgumentNullException(nameof(runtimeLeaseFactory));
        _acquireBeforeStart = acquireBeforeStart;
        _onRuntimeLeaseCreated = onRuntimeLeaseCreated;
        _cleanupOnStartFailure = cleanupOnStartFailure;
    }

    public async Task<TRuntimeLease> EnsureAsync(ProjectionMaterializationStartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectionKind);
        ct.ThrowIfCancellationRequested();

        await (_acquireBeforeStart?.Invoke(request, ct) ?? Task.CompletedTask);

        TContext? context = null;
        TRuntimeLease? runtimeLease = null;
        var started = false;
        try
        {
            context = _contextFactory(request, ct);
            runtimeLease = _runtimeLeaseFactory(context);
            await _lifecycle.StartAsync(runtimeLease, ct);
            started = true;

            await (_onRuntimeLeaseCreated?.Invoke(request, context, runtimeLease, ct) ?? Task.CompletedTask);
            return runtimeLease;
        }
        catch
        {
            if (started && runtimeLease != null)
            {
                try
                {
                    await _lifecycle.StopAsync(runtimeLease, CancellationToken.None);
                }
                catch
                {
                }
            }

            await (_cleanupOnStartFailure?.Invoke(request) ?? Task.CompletedTask);
            throw;
        }
    }
}
