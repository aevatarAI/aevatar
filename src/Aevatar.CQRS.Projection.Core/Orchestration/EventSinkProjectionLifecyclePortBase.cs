using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Event-sink specialized attach/release port base with runtime lease resolution hook.
/// </summary>
public abstract class EventSinkProjectionLifecyclePortBase<TLeaseContract, TRuntimeLease, TEvent>
    : IEventSinkProjectionLifecyclePort<TLeaseContract, TEvent>
    where TLeaseContract : class
    where TRuntimeLease : class, IProjectionRuntimeLease, TLeaseContract
    where TEvent : class
{
    private readonly Func<bool> _projectionEnabledAccessor;
    private readonly IProjectionScopeReleaseService<TRuntimeLease> _releaseService;
    private readonly IProjectionSessionEventHub<TEvent> _sessionEventHub;

    protected EventSinkProjectionLifecyclePortBase(
        Func<bool> projectionEnabledAccessor,
        IProjectionScopeReleaseService<TRuntimeLease> releaseService,
        IProjectionSessionEventHub<TEvent> sessionEventHub)
    {
        _projectionEnabledAccessor = projectionEnabledAccessor ?? throw new ArgumentNullException(nameof(projectionEnabledAccessor));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _sessionEventHub = sessionEventHub ?? throw new ArgumentNullException(nameof(sessionEventHub));
    }

    public bool ProjectionEnabled => _projectionEnabledAccessor();

    public async Task<IAsyncDisposable?> AttachLiveSinkAsync(
        TLeaseContract lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return null;

        // Refactor (iter101/cluster-104): Old lifecycle base exposed EnsureProjectionAsync to derived request-facing ports; new ports only attach sinks to leases that projection-owned activation already created.
        var runtimeLease = ResolveRuntimeLease(lease);
        if (runtimeLease is not IProjectionPortSessionLease portLease)
        {
            throw new InvalidOperationException(
                $"Runtime lease `{runtimeLease.GetType().FullName}` must implement `{typeof(IProjectionPortSessionLease).FullName}`.");
        }

        // Refactor (iter17/cluster-035): Old: ConcurrentDictionary registry. New: explicit IAsyncDisposable lease per attach.
        return await _sessionEventHub.SubscribeAsync(
            portLease.ScopeId,
            portLease.SessionId,
            evt => sink.PushAsync(evt, CancellationToken.None),
            ct).ConfigureAwait(false);
    }

    public async Task DetachLiveSinkAsync(
        IAsyncDisposable? liveSinkLease,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (liveSinkLease != null)
            await liveSinkLease.DisposeAsync().ConfigureAwait(false);
    }

    public Task ReleaseActorProjectionAsync(
        TLeaseContract lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return Task.CompletedTask;

        return _releaseService.ReleaseIfIdleAsync(ResolveRuntimeLease(lease), ct);
    }

    protected virtual TRuntimeLease ResolveRuntimeLease(TLeaseContract lease) =>
        lease as TRuntimeLease
        ?? throw new InvalidOperationException(
            $"Unsupported projection lease type `{lease.GetType().FullName}`; expected `{typeof(TRuntimeLease).FullName}`.");
}
