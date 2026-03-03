using System.Runtime.ExceptionServices;

namespace Aevatar.CQRS.Core.Abstractions.Streaming;

/// <summary>
/// Shared orchestration helpers for projection lease + event sink session lifecycle.
/// </summary>
public static class EventSinkProjectionLeaseOrchestrator
{
    public static async Task<TLease?> EnsureAndAttachAsync<TLease, TEvent>(
        Func<CancellationToken, Task<TLease?>> ensureAsync,
        Func<TLease, IEventSink<TEvent>, CancellationToken, Task> attachAsync,
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
        try
        {
            lease = await ensureAsync(ct);
            if (lease == null)
            {
                await sink.DisposeAsync();
                return null;
            }

            await attachAsync(lease, sink, ct);
            return lease;
        }
        catch
        {
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

    public static async Task DetachReleaseAndDisposeAsync<TLease, TEvent>(
        TLease? lease,
        IEventSink<TEvent> sink,
        Func<TLease, IEventSink<TEvent>, CancellationToken, Task> detachAsync,
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
                await detachAsync(lease, sink, CancellationToken.None);
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
