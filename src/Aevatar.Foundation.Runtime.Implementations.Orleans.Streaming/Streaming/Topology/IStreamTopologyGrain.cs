using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;

public interface IStreamTopologyGrain : IGrainWithStringKey
{
    Task UpsertAsync(StreamForwardingBindingEntry binding);

    Task RemoveAsync(string targetStreamId);

    Task<IReadOnlyList<StreamForwardingBindingEntry>> ListAsync();

    Task<long> GetRevisionAsync();

    Task ClearAsync();
}
