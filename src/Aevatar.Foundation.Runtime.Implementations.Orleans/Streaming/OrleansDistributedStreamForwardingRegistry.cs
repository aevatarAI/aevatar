using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class OrleansDistributedStreamForwardingRegistry(IGrainFactory grainFactory) : IStreamForwardingRegistry
{
    public Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.SourceStreamId);
        ct.ThrowIfCancellationRequested();

        var grain = grainFactory.GetGrain<IStreamTopologyGrain>(binding.SourceStreamId);
        return grain.UpsertAsync(binding);
    }

    public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);
        ct.ThrowIfCancellationRequested();

        var grain = grainFactory.GetGrain<IStreamTopologyGrain>(sourceStreamId);
        return grain.RemoveAsync(targetStreamId);
    }

    public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(string sourceStreamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ct.ThrowIfCancellationRequested();

        var grain = grainFactory.GetGrain<IStreamTopologyGrain>(sourceStreamId);
        return grain.ListAsync();
    }
}
