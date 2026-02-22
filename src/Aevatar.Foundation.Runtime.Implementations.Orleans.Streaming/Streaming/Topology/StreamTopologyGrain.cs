using Aevatar.Foundation.Abstractions.Streaming;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;

public sealed class StreamTopologyGrain(
    [PersistentState("stream-topology", OrleansRuntimeConstants.GrainStateStorageName)]
    IPersistentState<StreamTopologyGrainState> state) : Grain, IStreamTopologyGrain
{
    public async Task UpsertAsync(StreamForwardingBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.SourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.TargetStreamId);

        var existingIndex = state.State.Bindings.FindIndex(x =>
            string.Equals(x.TargetStreamId, binding.TargetStreamId, StringComparison.Ordinal));

        var entry = ToEntry(binding);
        if (existingIndex >= 0)
            state.State.Bindings[existingIndex] = entry;
        else
            state.State.Bindings.Add(entry);

        await state.WriteStateAsync();
    }

    public async Task RemoveAsync(string targetStreamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);

        var removed = state.State.Bindings.RemoveAll(x =>
            string.Equals(x.TargetStreamId, targetStreamId, StringComparison.Ordinal));
        if (removed > 0)
            await state.WriteStateAsync();
    }

    public Task<IReadOnlyList<StreamForwardingBinding>> ListAsync() =>
        Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(state.State.Bindings.Select(ToBinding).ToList());

    public async Task ClearAsync()
    {
        if (state.State.Bindings.Count == 0)
            return;

        state.State.Bindings.Clear();
        await state.WriteStateAsync();
    }

    private static StreamForwardingBindingEntry ToEntry(StreamForwardingBinding binding) =>
        new()
        {
            SourceStreamId = binding.SourceStreamId,
            TargetStreamId = binding.TargetStreamId,
            ForwardingMode = binding.ForwardingMode,
            DirectionFilter = binding.DirectionFilter.ToList(),
            EventTypeFilter = binding.EventTypeFilter.ToList(),
            Version = binding.Version,
            LeaseId = binding.LeaseId,
        };

    private static StreamForwardingBinding ToBinding(StreamForwardingBindingEntry entry) =>
        new()
        {
            SourceStreamId = entry.SourceStreamId,
            TargetStreamId = entry.TargetStreamId,
            ForwardingMode = entry.ForwardingMode,
            DirectionFilter = new HashSet<EventDirection>(entry.DirectionFilter),
            EventTypeFilter = new HashSet<string>(entry.EventTypeFilter, StringComparer.Ordinal),
            Version = entry.Version,
            LeaseId = entry.LeaseId,
        };
}
