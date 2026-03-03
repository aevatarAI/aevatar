namespace Aevatar.CQRS.Core.Abstractions.Streaming;

/// <summary>
/// Shared projection lifecycle operations for event-sink attach/detach/release orchestration.
/// </summary>
public interface IEventSinkProjectionLifecyclePort<TLease, TEvent>
{
    bool ProjectionEnabled { get; }

    Task AttachLiveSinkAsync(
        TLease lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default);

    Task DetachLiveSinkAsync(
        TLease lease,
        IEventSink<TEvent> sink,
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
    public static Task<TLease?> EnsureAndAttachAsync<TLease, TEvent>(
        this IEventSinkProjectionLifecyclePort<TLease, TEvent> lifecyclePort,
        Func<CancellationToken, Task<TLease?>> ensureAsync,
        IEventSink<TEvent> sink,
        CancellationToken ct = default)
        where TLease : class
    {
        ArgumentNullException.ThrowIfNull(lifecyclePort);
        ArgumentNullException.ThrowIfNull(ensureAsync);
        ArgumentNullException.ThrowIfNull(sink);

        return EventSinkProjectionLeaseOrchestrator.EnsureAndAttachAsync(
            ensureAsync,
            (lease, eventSink, token) => lifecyclePort.AttachLiveSinkAsync(lease, eventSink, token),
            (lease, token) => lifecyclePort.ReleaseActorProjectionAsync(lease, token),
            sink,
            ct);
    }

    public static Task DetachReleaseAndDisposeAsync<TLease, TEvent>(
        this IEventSinkProjectionLifecyclePort<TLease, TEvent> lifecyclePort,
        TLease? lease,
        IEventSink<TEvent> sink,
        Func<Task>? onDetachedAsync = null,
        CancellationToken ct = default)
        where TLease : class
    {
        ArgumentNullException.ThrowIfNull(lifecyclePort);
        ArgumentNullException.ThrowIfNull(sink);

        return EventSinkProjectionLeaseOrchestrator.DetachReleaseAndDisposeAsync(
            lease,
            sink,
            (runtimeLease, eventSink, token) => lifecyclePort.DetachLiveSinkAsync(runtimeLease, eventSink, token),
            (runtimeLease, token) => lifecyclePort.ReleaseActorProjectionAsync(runtimeLease, token),
            onDetachedAsync,
            ct);
    }
}
