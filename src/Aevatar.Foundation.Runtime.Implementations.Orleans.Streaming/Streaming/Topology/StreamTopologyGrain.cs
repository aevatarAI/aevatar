using Aevatar.Foundation.Abstractions.Streaming;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;

public sealed class StreamTopologyGrain(
    [PersistentState("stream-topology", OrleansRuntimeConstants.GrainStateStorageName)]
    IPersistentState<StreamTopologyGrainState> state) : Grain, IStreamTopologyGrain
{
    private IReadOnlyList<StreamForwardingBinding> _readSnapshot = Array.Empty<StreamForwardingBinding>();
    private bool _initialized;

    public async Task UpsertAsync(StreamForwardingBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.SourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.TargetStreamId);

        EnsureInitialized();
        var entry = ToEntry(binding);
        if (state.State.BindingsByTarget.TryGetValue(binding.TargetStreamId, out var existing) &&
            EntryEquals(existing, entry))
        {
            return;
        }

        state.State.BindingsByTarget[binding.TargetStreamId] = entry;
        state.State.Revision++;
        RebuildReadSnapshot();
        await state.WriteStateAsync();
    }

    public async Task RemoveAsync(string targetStreamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);

        EnsureInitialized();
        if (!state.State.BindingsByTarget.Remove(targetStreamId))
            return;

        state.State.Revision++;
        RebuildReadSnapshot();
        await state.WriteStateAsync();
    }

    public Task<IReadOnlyList<StreamForwardingBinding>> ListAsync()
    {
        EnsureInitialized();
        return Task.FromResult(_readSnapshot);
    }

    public Task<long> GetRevisionAsync()
    {
        EnsureInitialized();
        return Task.FromResult(state.State.Revision);
    }

    public async Task ClearAsync()
    {
        EnsureInitialized();
        if (state.State.BindingsByTarget.Count == 0)
            return;

        state.State.BindingsByTarget.Clear();
        state.State.Revision++;
        RebuildReadSnapshot();
        await state.WriteStateAsync();
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        if (state.State.BindingsByTarget.Count == 0 && state.State.Bindings.Count > 0)
        {
            foreach (var entry in state.State.Bindings)
            {
                if (string.IsNullOrWhiteSpace(entry.TargetStreamId))
                    continue;

                state.State.BindingsByTarget[entry.TargetStreamId] = CloneEntry(entry);
            }

            state.State.Bindings.Clear();
        }

        RebuildReadSnapshot();
        _initialized = true;
    }

    private void RebuildReadSnapshot()
    {
        if (state.State.BindingsByTarget.Count == 0)
        {
            _readSnapshot = Array.Empty<StreamForwardingBinding>();
            return;
        }

        var snapshot = new StreamForwardingBinding[state.State.BindingsByTarget.Count];
        var index = 0;
        foreach (var entry in state.State.BindingsByTarget.Values)
            snapshot[index++] = ToBinding(entry);

        _readSnapshot = snapshot;
    }

    private static StreamForwardingBindingEntry ToEntry(StreamForwardingBinding binding) =>
        new()
        {
            SourceStreamId = binding.SourceStreamId,
            TargetStreamId = binding.TargetStreamId,
            ForwardingMode = binding.ForwardingMode,
            DirectionFilter = binding.DirectionFilter.OrderBy(x => x).ToList(),
            EventTypeFilter = binding.EventTypeFilter.OrderBy(x => x, StringComparer.Ordinal).ToList(),
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

    private static StreamForwardingBindingEntry CloneEntry(StreamForwardingBindingEntry entry) =>
        new()
        {
            SourceStreamId = entry.SourceStreamId,
            TargetStreamId = entry.TargetStreamId,
            ForwardingMode = entry.ForwardingMode,
            DirectionFilter = [.. entry.DirectionFilter],
            EventTypeFilter = [.. entry.EventTypeFilter],
            Version = entry.Version,
            LeaseId = entry.LeaseId,
        };

    private static bool EntryEquals(StreamForwardingBindingEntry left, StreamForwardingBindingEntry right)
    {
        if (!string.Equals(left.SourceStreamId, right.SourceStreamId, StringComparison.Ordinal) ||
            !string.Equals(left.TargetStreamId, right.TargetStreamId, StringComparison.Ordinal) ||
            left.ForwardingMode != right.ForwardingMode ||
            left.Version != right.Version ||
            !string.Equals(left.LeaseId, right.LeaseId, StringComparison.Ordinal) ||
            left.DirectionFilter.Count != right.DirectionFilter.Count ||
            left.EventTypeFilter.Count != right.EventTypeFilter.Count)
        {
            return false;
        }

        for (var i = 0; i < left.DirectionFilter.Count; i++)
        {
            if (left.DirectionFilter[i] != right.DirectionFilter[i])
                return false;
        }

        for (var i = 0; i < left.EventTypeFilter.Count; i++)
        {
            if (!string.Equals(left.EventTypeFilter[i], right.EventTypeFilter[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
