namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ContextProjectionActivationService<TRuntimeLease, TContext, TTopology>
    : ProjectionActivationServiceBase<TRuntimeLease, TContext, TTopology>
    where TRuntimeLease : class
    where TContext : class, IProjectionContext
{
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
        : base(lifecycle)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _runtimeLeaseFactory = runtimeLeaseFactory ?? throw new ArgumentNullException(nameof(runtimeLeaseFactory));
        _acquireBeforeStart = acquireBeforeStart;
        _onRuntimeLeaseCreated = onRuntimeLeaseCreated;
        _cleanupOnStartFailure = cleanupOnStartFailure;
    }

    protected override Task AcquireBeforeStartAsync(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct) =>
        _acquireBeforeStart?.Invoke(rootEntityId, projectionName, input, commandId, ct) ?? Task.CompletedTask;

    protected override TContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct) =>
        _contextFactory(rootEntityId, projectionName, input, commandId, ct);

    protected override TRuntimeLease CreateRuntimeLease(TContext context) =>
        _runtimeLeaseFactory(context);

    protected override Task OnRuntimeLeaseCreatedAsync(
        string rootEntityId,
        string commandId,
        TContext context,
        TRuntimeLease runtimeLease,
        CancellationToken ct) =>
        _onRuntimeLeaseCreated?.Invoke(rootEntityId, commandId, context, runtimeLease, ct) ?? Task.CompletedTask;

    protected override Task CleanupOnStartFailureAsync(
        string rootEntityId,
        string commandId) =>
        _cleanupOnStartFailure?.Invoke(rootEntityId, commandId) ?? Task.CompletedTask;
}
