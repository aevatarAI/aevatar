using System.Collections.Concurrent;
using Orleans;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class OrleansStreamProviderAdapter : Aevatar.Foundation.Abstractions.IStreamProvider
{
    private readonly IClusterClient _clusterClient;
    private readonly AevatarOrleansRuntimeOptions _options;
    private readonly ConcurrentDictionary<string, OrleansActorStream> _streams = new(StringComparer.Ordinal);

    public OrleansStreamProviderAdapter(
        IClusterClient clusterClient,
        AevatarOrleansRuntimeOptions options)
    {
        _clusterClient = clusterClient;
        _options = options;
    }

    public IStream GetStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return _streams.GetOrAdd(actorId, id =>
        {
            var provider = _clusterClient.GetStreamProvider(_options.StreamProviderName);
            return new OrleansActorStream(id, _options.ActorEventNamespace, provider);
        });
    }

    public void RemoveStream(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        _streams.TryRemove(actorId, out _);
    }
}
