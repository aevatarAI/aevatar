using System.Threading;
using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Bootstrap.Extensions.AI;

/// <summary>
/// A lightweight wrapper that rebuilds the inner factory when config version changes.
/// Uses an immutable snapshot swapped atomically to avoid locks on the fast path.
/// </summary>
public sealed class ReloadableLLMProviderFactory : ILLMProviderFactory
{
    private sealed record Snapshot(ILLMProviderFactory Factory, long Version, long LastFailedVersion);

    private readonly Func<ILLMProviderFactory> _factoryBuilder;
    private readonly Func<long> _versionProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Immutable snapshot holding the current factory, version, and last-failed version.
    /// All mutations go through <see cref="Interlocked.CompareExchange{T}"/>.
    /// </summary>
    private Snapshot _snapshot;

    public ReloadableLLMProviderFactory(
        Func<ILLMProviderFactory> factoryBuilder,
        Func<long> versionProvider,
        ILogger? logger = null)
    {
        _factoryBuilder = factoryBuilder ?? throw new ArgumentNullException(nameof(factoryBuilder));
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _logger = logger ?? NullLogger.Instance;

        var factory = _factoryBuilder();
        var version = _versionProvider();
        _snapshot = new Snapshot(factory, version, long.MinValue);
    }

    public ILLMProvider GetProvider(string name) =>
        GetCurrentFactory().GetProvider(name);

    public ILLMProvider GetDefault() =>
        GetCurrentFactory().GetDefault();

    public IReadOnlyList<string> GetAvailableProviders() =>
        GetCurrentFactory().GetAvailableProviders();

    private ILLMProviderFactory GetCurrentFactory()
    {
        var current = Volatile.Read(ref _snapshot);
        var observedVersion = _versionProvider();

        // Fast path: version unchanged, no lock needed.
        if (observedVersion == current.Version)
            return current.Factory;

        // Slow path: version changed, attempt to rebuild.
        return RebuildFactory(current, observedVersion);
    }

    private ILLMProviderFactory RebuildFactory(Snapshot before, long observedVersion)
    {
        // Re-read to see if another thread already rebuilt for this version.
        var current = Volatile.Read(ref _snapshot);
        if (observedVersion == current.Version)
            return current.Factory;

        try
        {
            var newFactory = _factoryBuilder();
            var desired = new Snapshot(newFactory, observedVersion, long.MinValue);

            // Atomically swap only if no other thread has updated since we read.
            var original = Interlocked.CompareExchange(ref _snapshot, desired, current);
            if (ReferenceEquals(original, current))
                return newFactory;

            // Another thread won the race; use whatever is current now.
            return Volatile.Read(ref _snapshot).Factory;
        }
        catch (Exception ex)
        {
            // On failure, update LastFailedVersion to suppress repeated logging.
            if (observedVersion != current.LastFailedVersion)
            {
                var failed = current with { LastFailedVersion = observedVersion };
                Interlocked.CompareExchange(ref _snapshot, failed, current);

                _logger.LogWarning(
                    ex,
                    "Reloadable LLM provider factory failed to reload for version {Version}; keep previous snapshot.",
                    observedVersion);
            }

            return current.Factory;
        }
    }
}
