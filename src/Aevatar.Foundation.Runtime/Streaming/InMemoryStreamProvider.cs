// ─────────────────────────────────────────────────────────────
// InMemoryStreamProvider - in-memory stream provider.
// Creates or resolves InMemoryStream by actor ID and supports stream removal.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Streaming;

// DEV/TEST ONLY transport - production must use a durable Orleans/Kafka stream provider.
// Refactor (iter109/cluster-109-inmemory-stream-inline-dispatch):
//   Old pattern: Local stream runtime keeps actor-id stream registries and uses background Task.Run loops to invoke subscribers (DispatchSubscribersConcurrently fire-and-forgets each subscriber).
//   New principle: InMemoryStream is dev/test-only transport (usage proves no production registration); delete DispatchSubscribersConcurrently + fire-and-forget subscriber Task.Run; keep stream/forwarding registry but remove concurrent dispatch path; no new admission abstraction.
/// <summary>In-memory stream provider maintaining one event stream per actor.</summary>
public sealed class InMemoryStreamProvider :
    IStreamProvider,
    IStreamLifecycleNotifier,
    IStreamLifecycleManager
{
    private readonly ConcurrentDictionary<string, InMemoryStream> _streams = new();
    private readonly IStreamForwardingRegistry _forwardingRegistry;
    private readonly IStreamForwardingBindingSource _forwardingBindingSource;
    private readonly InMemoryStreamForwardingEngine _forwardingEngine;
    private readonly InMemoryStreamOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lock _callbacksLock = new();
    private volatile Action<string>[] _createdCallbacks = [];
    private volatile Action<string>[] _removedCallbacks = [];

    public InMemoryStreamProvider()
        : this(new InMemoryStreamOptions(), NullLoggerFactory.Instance, new InMemoryStreamForwardingRegistry())
    {
    }

    public InMemoryStreamProvider(
        InMemoryStreamOptions options,
        ILoggerFactory loggerFactory)
        : this(options, loggerFactory, new InMemoryStreamForwardingRegistry())
    {
    }

    public InMemoryStreamProvider(
        InMemoryStreamOptions options,
        ILoggerFactory loggerFactory,
        IStreamForwardingRegistry forwardingRegistry)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _forwardingRegistry = forwardingRegistry;
        _forwardingBindingSource = forwardingRegistry as IStreamForwardingBindingSource
            ?? throw new InvalidOperationException(
                $"{nameof(InMemoryStreamProvider)} requires forwarding registry implementing {nameof(IStreamForwardingBindingSource)}.");
        _forwardingEngine = new InMemoryStreamForwardingEngine(
            _forwardingBindingSource,
            streamId => GetStream(streamId),
            loggerFactory.CreateLogger<InMemoryStreamProvider>());
    }

    /// <summary>Gets or creates stream for specified actor.</summary>
    /// <param name="actorId">Unique actor identifier.</param>
    /// <returns>Actor event stream instance.</returns>
    public IStream GetStream(string actorId)
    {
        while (true)
        {
            if (_streams.TryGetValue(actorId, out var existing))
                return existing;

            // Refactor (iter88/cluster-088):
            // Old: ConcurrentDictionary.GetOrAdd ran a factory that constructed streams whose
            // constructor started background loops; losing factories could leak short-lived loops.
            // New: TryAdd selects the authoritative stream before Start/NotifyCreated run.
            var candidate = new InMemoryStream(
                actorId,
                _options,
                _loggerFactory.CreateLogger<InMemoryStream>(),
                envelope => _forwardingEngine.ForwardAsync(actorId, envelope),
                (binding, ct) => _forwardingRegistry.UpsertAsync(binding, ct),
                (targetStreamId, ct) => _forwardingRegistry.RemoveAsync(actorId, targetStreamId, ct),
                ct => _forwardingRegistry.ListBySourceAsync(actorId, ct),
                autoStart: false);

            if (_streams.TryAdd(actorId, candidate))
            {
                candidate.Start();
                NotifyCreated(actorId);
                return candidate;
            }

            candidate.Shutdown();
        }
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

        _forwardingBindingSource.RemoveByActor(actorId);
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
