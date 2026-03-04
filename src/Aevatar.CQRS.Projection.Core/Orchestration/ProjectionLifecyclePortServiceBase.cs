namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Generic lifecycle port base that centralizes projection enable-gate and lease/sink orchestration.
/// </summary>
public abstract class ProjectionLifecyclePortServiceBase<TLeaseContract, TRuntimeLease, TSink, TEvent>
    where TLeaseContract : class
    where TRuntimeLease : class, TLeaseContract
    where TSink : class
{
    private readonly Func<bool> _projectionEnabledAccessor;
    private readonly IProjectionPortActivationService<TRuntimeLease> _activationService;
    private readonly IProjectionPortReleaseService<TRuntimeLease> _releaseService;
    private readonly IProjectionPortSinkSubscriptionManager<TRuntimeLease, TSink, TEvent> _sinkSubscriptionManager;
    private readonly IProjectionPortLiveSinkForwarder<TRuntimeLease, TSink, TEvent> _liveSinkForwarder;

    protected ProjectionLifecyclePortServiceBase(
        Func<bool> projectionEnabledAccessor,
        IProjectionPortActivationService<TRuntimeLease> activationService,
        IProjectionPortReleaseService<TRuntimeLease> releaseService,
        IProjectionPortSinkSubscriptionManager<TRuntimeLease, TSink, TEvent> sinkSubscriptionManager,
        IProjectionPortLiveSinkForwarder<TRuntimeLease, TSink, TEvent> liveSinkForwarder)
    {
        _projectionEnabledAccessor = projectionEnabledAccessor ?? throw new ArgumentNullException(nameof(projectionEnabledAccessor));
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _sinkSubscriptionManager = sinkSubscriptionManager ?? throw new ArgumentNullException(nameof(sinkSubscriptionManager));
        _liveSinkForwarder = liveSinkForwarder ?? throw new ArgumentNullException(nameof(liveSinkForwarder));
    }

    protected bool ProjectionEnabledCore => _projectionEnabledAccessor();

    protected async Task<TLeaseContract?> EnsureProjectionAsync(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabledCore || string.IsNullOrWhiteSpace(rootEntityId))
            return null;

        return await _activationService.EnsureAsync(
            rootEntityId,
            projectionName,
            input,
            commandId,
            ct);
    }

    protected async Task AttachSinkAsync(
        TLeaseContract lease,
        TSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabledCore)
            return;

        var runtimeLease = ResolveRuntimeLease(lease);
        await _sinkSubscriptionManager.AttachOrReplaceAsync(
            runtimeLease,
            sink,
            evt => _liveSinkForwarder.ForwardAsync(runtimeLease, sink, evt, CancellationToken.None),
            ct);
    }

    protected async Task DetachSinkAsync(
        TLeaseContract lease,
        TSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabledCore)
            return;

        var runtimeLease = ResolveRuntimeLease(lease);
        await _sinkSubscriptionManager.DetachAsync(runtimeLease, sink, ct);
    }

    protected async Task ReleaseProjectionAsync(
        TLeaseContract lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabledCore)
            return;

        var runtimeLease = ResolveRuntimeLease(lease);
        await _releaseService.ReleaseIfIdleAsync(runtimeLease, ct);
    }

    protected virtual TRuntimeLease ResolveRuntimeLease(TLeaseContract lease) =>
        lease as TRuntimeLease
        ?? throw new InvalidOperationException(
            $"Unsupported projection lease type `{lease.GetType().FullName}`; expected `{typeof(TRuntimeLease).FullName}`.");
}
