using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

internal sealed class StreamProviderLifecycleManager : IStreamLifecycleManager
{
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streamProvider;
    private readonly ILogger<StreamProviderLifecycleManager> _logger;

    public StreamProviderLifecycleManager(
        Aevatar.Foundation.Abstractions.IStreamProvider streamProvider,
        ILogger<StreamProviderLifecycleManager>? logger = null)
    {
        _streamProvider = streamProvider;
        _logger = logger ?? NullLogger<StreamProviderLifecycleManager>.Instance;
    }

    public void RemoveStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        if (_streamProvider is IStreamCacheManager cacheManager)
        {
            cacheManager.RemoveStream(actorId);
            return;
        }

        _logger.LogDebug(
            "Stream provider {ProviderType} does not support cache eviction for actor {ActorId}.",
            _streamProvider.GetType().Name,
            actorId);
    }
}
