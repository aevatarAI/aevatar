using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;

public interface IStreamTopologyGrain : IGrainWithStringKey
{
    Task UpsertAsync(StreamForwardingBinding binding);

    Task RemoveAsync(string targetStreamId);

    Task<IReadOnlyList<StreamForwardingBinding>> ListAsync();

    Task<long> GetRevisionAsync();

    Task ClearAsync();
}
