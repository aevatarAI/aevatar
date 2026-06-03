using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Event-sink specialized attach/release port base with runtime lease resolution hook.
/// </summary>
// Refactor (iter367/cluster-issue377): Old pattern: session sink attach required IProjectionPortSessionLease.
// Refactor (iter367/cluster-issue377): Old pattern: the alias only forwarded RootActorId and SessionId.
// Refactor (iter367/cluster-issue377): New principle: typed projection session context owns routing identity.
// Refactor (iter367/cluster-issue377): New principle: lifecycle attach reads RootActorId and SessionId from Context.
public abstract class EventSinkProjectionLifecyclePortBase<TLeaseContract, TRuntimeLease, TEvent>
    : IEventSinkProjectionLifecyclePort<TLeaseContract, TEvent>
    where TLeaseContract : class
    where TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<IProjectionSessionContext>, TLeaseContract
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

        // Refactor (iter367/cluster-issue377): Old pattern: attach inspected IProjectionPortSessionLease alias properties.
        // Refactor (iter367/cluster-issue377): Old pattern: alias let leases duplicate RootActorId as ScopeId.
        // Refactor (iter367/cluster-issue377): New principle: RootActorId + SessionId come from the typed session context.
        // Refactor (iter367/cluster-issue377): New principle: the runtime lease only carries context, not a routing alias.
        var runtimeLease = ResolveRuntimeLease(lease);
        var sessionContext = runtimeLease.Context;

        // Refactor (iter17/cluster-035): Old: ConcurrentDictionary registry. New: explicit IAsyncDisposable lease per attach.
        return await _sessionEventHub.SubscribeAsync(
            sessionContext.RootActorId,
            sessionContext.SessionId,
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

    // Refactor (iter367/cluster-issue377): Old pattern: resolved leases needed a second alias interface check.
    // Refactor (iter367/cluster-issue377): Old pattern: unsupported leases failed after alias casting.
    // Refactor (iter367/cluster-issue377): New principle: generic constraints guarantee typed context availability.
    // Refactor (iter367/cluster-issue377): New principle: this method only validates the public lease contract shape.
    protected virtual TRuntimeLease ResolveRuntimeLease(TLeaseContract lease) =>
        lease as TRuntimeLease
        ?? throw new InvalidOperationException(
            $"Unsupported projection lease type `{lease.GetType().FullName}`; expected `{typeof(TRuntimeLease).FullName}`.");
}
