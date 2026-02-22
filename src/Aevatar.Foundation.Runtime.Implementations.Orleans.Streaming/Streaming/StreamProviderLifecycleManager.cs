using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

internal sealed class StreamProviderLifecycleManager : IStreamLifecycleManager
{
    private readonly OrleansStreamProviderAdapter _streamProvider;
    private readonly ILogger<StreamProviderLifecycleManager> _logger;

    public StreamProviderLifecycleManager(
        OrleansStreamProviderAdapter streamProvider,
        ILogger<StreamProviderLifecycleManager>? logger = null)
    {
        _streamProvider = streamProvider;
        _logger = logger ?? NullLogger<StreamProviderLifecycleManager>.Instance;
    }

    public void RemoveStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        _streamProvider.RemoveStream(actorId);
        _logger.LogDebug("Orleans stream cache eviction executed for actor {ActorId}.", actorId);
    }
}
