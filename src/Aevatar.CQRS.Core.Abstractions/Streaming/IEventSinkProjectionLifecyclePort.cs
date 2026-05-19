namespace Aevatar.CQRS.Core.Abstractions.Streaming;

/// <summary>
/// Shared projection lifecycle operations for event-sink attach/detach/release orchestration.
/// </summary>
public interface IEventSinkProjectionLifecyclePort<TLease, TEvent>
{
    bool ProjectionEnabled { get; }

    // Refactor (iter17/cluster-035): Old pattern: attach hid live sink subscriptions behind port-owned process registries. New principle: attach returns an explicit cleanup lease owned by the caller.
    Task<IAsyncDisposable?> AttachLiveSinkAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default);

    // Refactor (iter17/cluster-035): Old pattern: detach looked up subscriptions by projection lease and sink. New principle: detach consumes the exact live sink lease produced by attach.
    Task DetachLiveSinkAsync(
        IAsyncDisposable? liveSinkLease,
        CancellationToken ct = default);

    Task ReleaseActorProjectionAsync(
        TLease lease,
        CancellationToken ct = default);
}

/// <summary>
/// Helpers that combine lifecycle port operations with lease/sink orchestration templates.
/// </summary>
public static class EventSinkProjectionLifecyclePortExtensions
{
    // Refactor (iter17/cluster-035): Old pattern: extension returned only the actor projection lease and made live sink cleanup unreachable. New principle: extension returns the full attachment with both projection and live sink leases.
    public static Task<EventSinkProjectionAttachment<TLease>?> EnsureAndAttachLeaseAsync<TLease, TEvent>(
        this IEventSinkProjectionLifecyclePort<TLease, TEvent> lifecyclePort,
        Func<CancellationToken, Task<TLease?>> ensureAsync,
        IEventSink<TEvent> sink,
        CancellationToken ct = default)
        where TLease : class
    {
        ArgumentNullException.ThrowIfNull(lifecyclePort);
        ArgumentNullException.ThrowIfNull(ensureAsync);
        ArgumentNullException.ThrowIfNull(sink);

        return EventSinkProjectionLeaseOrchestrator.EnsureAndAttachLeaseAsync(
            ensureAsync,
            (lease, eventSink, token) => lifecyclePort.AttachLiveSinkAsync(lease, eventSink, token),
            (lease, token) => lifecyclePort.ReleaseActorProjectionAsync(lease, token),
            sink,
            ct);
    }

    // Refactor (iter17/cluster-035): Old pattern: cleanup depended on hidden actorId-to-subscription lookup. New principle: cleanup is driven by the explicit live sink lease carried from attach.
    public static Task DetachReleaseAndDisposeAsync<TLease, TEvent>(
        this IEventSinkProjectionLifecyclePort<TLease, TEvent> lifecyclePort,
        TLease? lease,
        IAsyncDisposable? liveSinkLease,
        IEventSink<TEvent> sink,
        Func<Task>? onDetachedAsync = null,
        CancellationToken ct = default)
        where TLease : class
    {
        ArgumentNullException.ThrowIfNull(lifecyclePort);
        ArgumentNullException.ThrowIfNull(sink);

        return EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync(
            lease,
            liveSinkLease,
            sink,
            (subscriptionLease, token) => lifecyclePort.DetachLiveSinkAsync(subscriptionLease, token),
            (runtimeLease, token) => lifecyclePort.ReleaseActorProjectionAsync(runtimeLease, token),
            onDetachedAsync,
            ct);
    }
}

// Refactor (iter17/cluster-035): Old pattern: attach returned only a projection lease and discarded the live sink subscription lease. New principle: attachment carries every lifecycle handle required for deterministic detach.
public sealed record EventSinkProjectionAttachment<TLease>(
    TLease ProjectionLease,
    IAsyncDisposable? LiveSinkLease);
