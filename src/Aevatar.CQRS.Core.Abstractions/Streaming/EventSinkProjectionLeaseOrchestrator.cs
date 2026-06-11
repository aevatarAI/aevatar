using System.Runtime.ExceptionServices;

namespace Aevatar.CQRS.Core.Abstractions.Streaming;

/// <summary>
/// Shared orchestration helpers for projection lease + event sink session lifecycle.
/// </summary>
public static class EventSinkProjectionLeaseOrchestrator
{
    // Refactor (iter17/cluster-035): Old pattern: helper returned only the projection lease and lost the live sink cleanup handle. New principle: attach returns the full attachment so callers own both lifecycle leases explicitly.
    public static async Task<EventSinkProjectionAttachment<TLease>?> EnsureAndAttachLeaseAsync<TLease, TEvent>(
        Func<CancellationToken, Task<TLease?>> ensureAsync,
        Func<TLease, IEventSink<TEvent>, CancellationToken, Task<IAsyncDisposable?>> attachAsync,
        Func<TLease, CancellationToken, Task> releaseAsync,
        IEventSink<TEvent> sink,
        CancellationToken ct = default)
        where TLease : class
    {
        ArgumentNullException.ThrowIfNull(ensureAsync);
        ArgumentNullException.ThrowIfNull(attachAsync);
        ArgumentNullException.ThrowIfNull(releaseAsync);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        TLease? lease = null;
        IAsyncDisposable? liveSinkLease = null;
        try
        {
            lease = await ensureAsync(ct);
            if (lease == null)
            {
                await sink.DisposeAsync();
                return null;
            }

            liveSinkLease = await attachAsync(lease, sink, ct);
            return new EventSinkProjectionAttachment<TLease>(lease, liveSinkLease);
        }
        catch
        {
            if (liveSinkLease != null)
            {
                try
                {
                    await liveSinkLease.DisposeAsync();
                }
                catch
                {
                    // Best effort cleanup path.
                }
            }

            if (lease != null)
            {
                try
                {
                    await releaseAsync(lease, CancellationToken.None);
                }
                catch
                {
                    // Best effort cleanup path.
                }
            }

            await sink.DisposeAsync();
            throw;
        }
    }

    // Refactor (iter17/cluster-035): Old pattern: detach found subscriptions through hidden process-local registries. New principle: callers pass the live sink lease returned by attach so cleanup is explicit and runtime-neutral.
    public static async Task DetachReleaseAndDisposeAsync<TLease, TEvent>(
        TLease? lease,
        IAsyncDisposable? liveSinkLease,
        IEventSink<TEvent> sink,
        Func<IAsyncDisposable?, CancellationToken, Task> detachAsync,
        Func<TLease, CancellationToken, Task> releaseAsync,
        Func<Task>? onDetachedAsync = null,
        CancellationToken ct = default)
        where TLease : class
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(detachAsync);
        ArgumentNullException.ThrowIfNull(releaseAsync);
        ct.ThrowIfCancellationRequested();

        Exception? firstException = null;

        if (lease != null)
        {
            try
            {
                await detachAsync(liveSinkLease, CancellationToken.None);
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (onDetachedAsync != null)
        {
            try
            {
                await onDetachedAsync();
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (lease != null)
        {
            try
            {
                await releaseAsync(lease, CancellationToken.None);
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        try
        {
            sink.Complete();
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        try
        {
            await sink.DisposeAsync();
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }
}
