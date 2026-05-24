using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Abstractions.Behaviors;
using Google.Protobuf;
using Microsoft.Extensions.Caching.Memory;

namespace Aevatar.Scripting.Infrastructure.Compilation;

// Refactor (iter90/cluster-090-script-artifact-cache-retention):
//   Old: Singleton resolver kept a ConcurrentDictionary<string, Lazy<...>> forever, used delimiter-concatenated keys, and cached failed Lazy values.
//   New: Use a bounded MemoryCache keyed by a typed composite key, evict by size, and remove failed Lazy entries so transient compile failures can retry.
public sealed class CachedScriptBehaviorArtifactResolver : IScriptBehaviorArtifactResolver, IDisposable
{
    private const long CacheEntrySize = 1;
    private const long DefaultMaxCachedArtifacts = 256;
    private static readonly TimeSpan DefaultSlidingExpiration = TimeSpan.FromMinutes(30);

    private readonly MemoryCache _artifacts;
    private readonly object _cacheGate = new();
    private readonly IScriptBehaviorCompiler _compiler;
    private readonly long _maxCachedArtifacts;
    private readonly TimeSpan _slidingExpiration;

    public CachedScriptBehaviorArtifactResolver(IScriptBehaviorCompiler compiler)
        : this(compiler, DefaultMaxCachedArtifacts, DefaultSlidingExpiration)
    {
    }

    public CachedScriptBehaviorArtifactResolver(
        IScriptBehaviorCompiler compiler,
        long maxCachedArtifacts,
        TimeSpan? slidingExpiration = null)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        if (maxCachedArtifacts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCachedArtifacts), "Artifact cache size limit must be positive.");

        _slidingExpiration = slidingExpiration ?? DefaultSlidingExpiration;
        _maxCachedArtifacts = maxCachedArtifacts;
        _artifacts = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = maxCachedArtifacts,
        });
    }

    public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cacheKey = ScriptBehaviorArtifactCacheKey.From(request);
        while (true)
        {
            var entry = GetOrCreateEntry(cacheKey, request);

            try
            {
                return entry.LeaseForCaller();
            }
            catch (EvictedArtifactCacheEntryDisposedException)
            {
                RemoveFailedLazy(cacheKey, entry);
            }
            catch
            {
                RemoveFailedLazy(cacheKey, entry);
                throw;
            }
        }
    }

    public void Dispose()
    {
        _artifacts.Dispose();
    }

    private ArtifactCacheEntry GetOrCreateEntry(
        ScriptBehaviorArtifactCacheKey cacheKey,
        ScriptBehaviorArtifactRequest request)
    {
        if (_artifacts.TryGetValue(cacheKey, out ArtifactCacheEntry? existing) && existing != null)
            return existing;

        lock (_cacheGate)
        {
            if (_artifacts.TryGetValue(cacheKey, out existing) && existing != null)
                return existing;

            var created = new ArtifactCacheEntry(() => CompileOrThrow(request));

            CompactWhenFull();
            _artifacts.Set(
                cacheKey,
                created,
                new MemoryCacheEntryOptions()
                    .SetSize(CacheEntrySize)
                    .SetSlidingExpiration(_slidingExpiration)
                    .RegisterPostEvictionCallback(DisposeEvictedArtifact));

            return created;
        }
    }

    private void CompactWhenFull()
    {
        if (_artifacts.Count < _maxCachedArtifacts)
            return;

        _artifacts.Compact(1.0 / _maxCachedArtifacts);
    }

    private void RemoveFailedLazy(
        ScriptBehaviorArtifactCacheKey cacheKey,
        ArtifactCacheEntry failed)
    {
        lock (_cacheGate)
        {
            if (_artifacts.TryGetValue(cacheKey, out ArtifactCacheEntry? current) &&
                ReferenceEquals(current, failed))
            {
                _artifacts.Remove(cacheKey);
            }
        }
    }

    private ScriptBehaviorArtifact CompileOrThrow(ScriptBehaviorArtifactRequest request)
    {
        var compilation = _compiler.Compile(request.ToCompilationRequest());
        if (!compilation.IsSuccess || compilation.Artifact == null)
        {
            throw new InvalidOperationException(
                "Script artifact resolution failed: " + string.Join("; ", compilation.Diagnostics));
        }

        return compilation.Artifact;
    }

    private static void DisposeEvictedArtifact(object key, object? value, EvictionReason reason, object? state)
    {
        _ = key;
        _ = reason;
        _ = state;

        if (value is not ArtifactCacheEntry entry)
            return;

        entry.MarkEvicted();
    }

    private sealed class ArtifactCacheEntry
    {
        private readonly object _gate = new();
        private readonly Lazy<ScriptBehaviorArtifact> _lazy;
        private ScriptBehaviorArtifact? _artifact;
        private int _referenceCount;
        private bool _evicted;
        private bool _disposeStarted;

        public ArtifactCacheEntry(Func<ScriptBehaviorArtifact> artifactFactory)
        {
            _lazy = new Lazy<ScriptBehaviorArtifact>(
                () =>
                {
                    var artifact = artifactFactory();
                    bool disposeNow = false;
                    lock (_gate)
                    {
                        _artifact = artifact;
                        if (_evicted && _referenceCount == 0 && !_disposeStarted)
                        {
                            _disposeStarted = true;
                            disposeNow = true;
                        }
                    }

                    if (disposeNow)
                        DisposeArtifact(artifact);

                    return artifact;
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public ScriptBehaviorArtifact LeaseForCaller()
        {
            var callerLease = Retain();
            try
            {
                var artifact = _lazy.Value;
                return new ScriptBehaviorArtifact(
                    artifact.ScriptId,
                    artifact.Revision,
                    artifact.PackageHash,
                    artifact.Descriptor,
                    artifact.Contract,
                    () => CreateBehavior(artifact, callerLease),
                    callerLease.ReleaseAsync);
            }
            catch
            {
                callerLease.Release();
                throw;
            }
        }

        public void MarkEvicted()
        {
            ScriptBehaviorArtifact? disposeNow = null;
            lock (_gate)
            {
                if (_evicted)
                    return;

                _evicted = true;
                if (_artifact != null && _referenceCount == 0 && !_disposeStarted)
                {
                    _disposeStarted = true;
                    disposeNow = _artifact;
                }
            }

            if (disposeNow != null)
                DisposeArtifact(disposeNow);
        }

        private ArtifactLease Retain()
        {
            lock (_gate)
            {
                if (_disposeStarted)
                    throw new EvictedArtifactCacheEntryDisposedException();

                _referenceCount += 1;
            }

            return new ArtifactLease(this);
        }

        private IScriptBehaviorBridge CreateBehavior(ScriptBehaviorArtifact artifact, ArtifactLease callerLease)
        {
            var behaviorLease = Retain();
            try
            {
                var behavior = artifact.CreateBehavior();
                callerLease.Release();
                return new LeasedScriptBehaviorBridge(behavior, behaviorLease);
            }
            catch
            {
                behaviorLease.Release();
                callerLease.Release();
                throw;
            }
        }

        private ValueTask ReleaseAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }

        private void Release()
        {
            ScriptBehaviorArtifact? disposeNow = null;
            lock (_gate)
            {
                if (_referenceCount == 0)
                    return;

                _referenceCount -= 1;
                if (_evicted && _referenceCount == 0 && _artifact != null && !_disposeStarted)
                {
                    _disposeStarted = true;
                    disposeNow = _artifact;
                }
            }

            if (disposeNow != null)
                DisposeArtifact(disposeNow);
        }

        private static void DisposeArtifact(ScriptBehaviorArtifact artifact)
        {
            try
            {
                var dispose = artifact.DisposeAsync();
                if (!dispose.IsCompletedSuccessfully)
                {
                    _ = dispose.AsTask().ContinueWith(
                        static task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            catch
            {
                // Eviction must not make cache mutation fail; callers already observe compile failures through Resolve.
            }
        }

        public sealed class ArtifactLease
        {
            private readonly ArtifactCacheEntry _owner;
            private int _released;

            public ArtifactLease(ArtifactCacheEntry owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public ValueTask ReleaseAsync()
            {
                Release();
                return ValueTask.CompletedTask;
            }

            public void Release()
            {
                if (Interlocked.CompareExchange(ref _released, 1, 0) == 0)
                    _owner.Release();
            }
        }
    }

    private sealed class LeasedScriptBehaviorBridge : IScriptBehaviorBridge, IDisposable, IAsyncDisposable
    {
        private readonly IScriptBehaviorBridge _inner;
        private readonly ArtifactCacheEntry.ArtifactLease _lease;
        private int _disposed;

        public LeasedScriptBehaviorBridge(IScriptBehaviorBridge inner, ArtifactCacheEntry.ArtifactLease lease)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        }

        public ScriptBehaviorDescriptor Descriptor => _inner.Descriptor;

        public Task<IReadOnlyList<IMessage>> DispatchAsync(
            IMessage inbound,
            ScriptDispatchContext context,
            CancellationToken ct) =>
            _inner.DispatchAsync(inbound, context, ct);

        public IMessage? ApplyDomainEvent(
            IMessage? currentState,
            IMessage domainEvent,
            ScriptFactContext context) =>
            _inner.ApplyDomainEvent(currentState, domainEvent, context);

        public IMessage? BuildReadModel(
            IMessage? currentState,
            ScriptFactContext context) =>
            _inner.BuildReadModel(currentState, context);

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            if (_inner is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                finally
                {
                    _lease.Release();
                }

                return;
            }

            if (_inner is IAsyncDisposable asyncDisposable)
            {
                DisposeAsyncBehavior(asyncDisposable, _lease);
                return;
            }

            _lease.Release();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            try
            {
                if (_inner is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else if (_inner is IDisposable disposable)
                    disposable.Dispose();
            }
            finally
            {
                _lease.Release();
            }
        }

        private static void DisposeAsyncBehavior(
            IAsyncDisposable asyncDisposable,
            ArtifactCacheEntry.ArtifactLease lease)
        {
            try
            {
                var dispose = asyncDisposable.DisposeAsync();
                if (dispose.IsCompletedSuccessfully)
                {
                    lease.Release();
                    return;
                }

                _ = dispose.AsTask().ContinueWith(
                    static (task, state) =>
                    {
                        _ = task.Exception;
                        ((ArtifactCacheEntry.ArtifactLease)state!).Release();
                    },
                    lease,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch
            {
                lease.Release();
                throw;
            }
        }

    }

    private sealed class EvictedArtifactCacheEntryDisposedException : Exception
    {
    }

    private readonly record struct ScriptBehaviorArtifactCacheKey(
        string ScriptId,
        string Revision,
        string ResolvedPackageHash,
        string EntryBehaviorTypeName)
    {
        public static ScriptBehaviorArtifactCacheKey From(ScriptBehaviorArtifactRequest request) =>
            new(
                request.ScriptId ?? string.Empty,
                request.Revision ?? string.Empty,
                request.ResolvedPackageHash ?? string.Empty,
                request.Package.EntryBehaviorTypeName ?? string.Empty);
    }
}
