using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;
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
        var lazy = GetOrCreateLazy(cacheKey, request);

        try
        {
            return lazy.Value;
        }
        catch
        {
            RemoveFailedLazy(cacheKey, lazy);
            throw;
        }
    }

    public void Dispose()
    {
        _artifacts.Dispose();
    }

    private Lazy<ScriptBehaviorArtifact> GetOrCreateLazy(
        ScriptBehaviorArtifactCacheKey cacheKey,
        ScriptBehaviorArtifactRequest request)
    {
        if (_artifacts.TryGetValue(cacheKey, out Lazy<ScriptBehaviorArtifact>? existing) && existing != null)
            return existing;

        lock (_cacheGate)
        {
            if (_artifacts.TryGetValue(cacheKey, out existing) && existing != null)
                return existing;

            var created = new Lazy<ScriptBehaviorArtifact>(
                () => CompileOrThrow(request),
                LazyThreadSafetyMode.ExecutionAndPublication);

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
        Lazy<ScriptBehaviorArtifact> failed)
    {
        lock (_cacheGate)
        {
            if (_artifacts.TryGetValue(cacheKey, out Lazy<ScriptBehaviorArtifact>? current) &&
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

        if (value is not Lazy<ScriptBehaviorArtifact> lazy || !lazy.IsValueCreated)
            return;

        try
        {
            var dispose = lazy.Value.DisposeAsync();
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
