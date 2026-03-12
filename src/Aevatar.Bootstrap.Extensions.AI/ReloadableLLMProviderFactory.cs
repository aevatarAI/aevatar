using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Bootstrap.Extensions.AI;

/// <summary>
/// A lightweight wrapper that rebuilds the inner factory when config version changes.
/// </summary>
public sealed class ReloadableLLMProviderFactory : ILLMProviderFactory
{
    private readonly Func<ILLMProviderFactory> _factoryBuilder;
    private readonly Func<long> _versionProvider;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    private ILLMProviderFactory _currentFactory;
    private long _currentVersion;
    private long _lastFailedVersion = long.MinValue;

    public ReloadableLLMProviderFactory(
        Func<ILLMProviderFactory> factoryBuilder,
        Func<long> versionProvider,
        ILogger? logger = null)
    {
        _factoryBuilder = factoryBuilder ?? throw new ArgumentNullException(nameof(factoryBuilder));
        _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
        _logger = logger ?? NullLogger.Instance;

        _currentFactory = _factoryBuilder();
        _currentVersion = _versionProvider();
    }

    public ILLMProvider GetProvider(string name) =>
        GetCurrentFactory().GetProvider(name);

    public ILLMProvider GetDefault() =>
        GetCurrentFactory().GetDefault();

    public IReadOnlyList<string> GetAvailableProviders() =>
        GetCurrentFactory().GetAvailableProviders();

    private ILLMProviderFactory GetCurrentFactory()
    {
        lock (_sync)
        {
            var observedVersion = _versionProvider();
            if (observedVersion == _currentVersion)
                return _currentFactory;

            try
            {
                _currentFactory = _factoryBuilder();
                _currentVersion = observedVersion;
                _lastFailedVersion = long.MinValue;
            }
            catch (Exception ex)
            {
                if (observedVersion != _lastFailedVersion)
                {
                    _lastFailedVersion = observedVersion;
                    _logger.LogWarning(
                        ex,
                        "Reloadable LLM provider factory failed to reload for version {Version}; keep previous snapshot.",
                        observedVersion);
                }
            }

            return _currentFactory;
        }
    }
}
