// ─────────────────────────────────────────────────────────────
// InMemoryStreamProvider - in-memory stream provider.
// Creates or resolves InMemoryStream by actor ID and supports stream removal.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Streaming;

/// <summary>In-memory stream provider maintaining one event stream per actor.</summary>
public sealed class InMemoryStreamProvider : IStreamProvider, IStreamLifecycleNotifier
{
    private readonly ConcurrentDictionary<string, InMemoryStream> _streams = new();
    private readonly InMemoryStreamOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lock _callbacksLock = new();
    private volatile Action<string>[] _createdCallbacks = [];
    private volatile Action<string>[] _removedCallbacks = [];

    public InMemoryStreamProvider()
        : this(new InMemoryStreamOptions(), NullLoggerFactory.Instance)
    {
    }

    public InMemoryStreamProvider(
        InMemoryStreamOptions options,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Gets or creates stream for specified actor.</summary>
    /// <param name="actorId">Unique actor identifier.</param>
    /// <returns>Actor event stream instance.</returns>
    public IStream GetStream(string actorId)
    {
        var created = false;
        var stream = _streams.GetOrAdd(actorId, id =>
        {
            created = true;
            return new InMemoryStream(
                id,
                _options,
                _loggerFactory.CreateLogger<InMemoryStream>());
        });

        if (created)
            NotifyCreated(actorId);

        return stream;
    }

    /// <summary>Removes and shuts down stream for specified actor.</summary>
    /// <param name="actorId">Actor ID to remove.</param>
    public void RemoveStream(string actorId)
    {
        if (_streams.TryRemove(actorId, out var stream))
        {
            stream.Shutdown();
            NotifyRemoved(actorId);
        }
    }

    public IDisposable SubscribeCreated(Action<string> onCreated)
    {
        ArgumentNullException.ThrowIfNull(onCreated);
        lock (_callbacksLock)
        {
            var current = _createdCallbacks;
            var next = new Action<string>[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = onCreated;
            _createdCallbacks = next;
        }

        return new CallbackSubscription(() =>
        {
            lock (_callbacksLock)
            {
                _createdCallbacks = _createdCallbacks.Where(x => x != onCreated).ToArray();
            }
        });
    }

    public IDisposable SubscribeRemoved(Action<string> onRemoved)
    {
        ArgumentNullException.ThrowIfNull(onRemoved);
        lock (_callbacksLock)
        {
            var current = _removedCallbacks;
            var next = new Action<string>[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = onRemoved;
            _removedCallbacks = next;
        }

        return new CallbackSubscription(() =>
        {
            lock (_callbacksLock)
            {
                _removedCallbacks = _removedCallbacks.Where(x => x != onRemoved).ToArray();
            }
        });
    }

    private void NotifyCreated(string actorId)
    {
        var callbacks = _createdCallbacks;
        foreach (var callback in callbacks)
        {
            try
            {
                callback(actorId);
            }
            catch
            {
                // best-effort callback
            }
        }
    }

    private void NotifyRemoved(string actorId)
    {
        var callbacks = _removedCallbacks;
        foreach (var callback in callbacks)
        {
            try
            {
                callback(actorId);
            }
            catch
            {
                // best-effort callback
            }
        }
    }

    private sealed class CallbackSubscription : IDisposable
    {
        private readonly Action _dispose;
        private int _disposed;

        public CallbackSubscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;
            _dispose();
        }
    }
}
