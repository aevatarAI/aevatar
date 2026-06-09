using Aevatar.Foundation.Abstractions.Streaming;
using Orleans.Runtime;
using Orleans.Storage;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;

public sealed class StreamTopologyGrain(
    [PersistentState("stream-topology", OrleansRuntimeConstants.GrainStateStorageName)]
    IPersistentState<StreamTopologyGrainState> state) : Grain, IStreamTopologyGrain
{
    private const int MaxStorageWriteAttempts = 3;

    private IReadOnlyList<StreamForwardingBindingEntry> _readSnapshot = Array.Empty<StreamForwardingBindingEntry>();
    private bool _initialized;

    public Task UpsertAsync(StreamForwardingBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return UpsertAsync(ToEntry(binding));
    }

    public async Task UpsertAsync(StreamForwardingBindingEntry binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.SourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.TargetStreamId);

        var entry = CloneEntry(binding);
        await WriteWithConflictRetryAsync(() => UpsertEntry(entry));
    }

    public async Task RemoveAsync(string targetStreamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);

        await WriteWithConflictRetryAsync(() => RemoveEntry(targetStreamId));
    }

    public Task<IReadOnlyList<StreamForwardingBindingEntry>> ListAsync()
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
        await WriteWithConflictRetryAsync(ClearEntries);
    }

    private async Task WriteWithConflictRetryAsync(Func<bool> applyMutation)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (!applyMutation())
                return;

            RebuildReadSnapshot();

            try
            {
                await state.WriteStateAsync();
                return;
            }
            catch (InconsistentStateException) when (attempt < MaxStorageWriteAttempts)
            {
                await state.ReadStateAsync();
                _initialized = false;
            }
        }
    }

    private bool UpsertEntry(StreamForwardingBindingEntry entry)
    {
        EnsureInitialized();
        if (state.State.BindingsByTarget.TryGetValue(entry.TargetStreamId, out var existing) &&
            EntryEquals(existing, entry))
        {
            return false;
        }

        state.State.BindingsByTarget[entry.TargetStreamId] = CloneEntry(entry);
        state.State.Revision++;
        return true;
    }

    private bool RemoveEntry(string targetStreamId)
    {
        EnsureInitialized();
        if (!state.State.BindingsByTarget.Remove(targetStreamId))
            return false;

        state.State.Revision++;
        return true;
    }

    private bool ClearEntries()
    {
        EnsureInitialized();
        if (state.State.BindingsByTarget.Count == 0)
            return false;

        state.State.BindingsByTarget.Clear();
        state.State.Revision++;
        return true;
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
            _readSnapshot = Array.Empty<StreamForwardingBindingEntry>();
            return;
        }

        var snapshot = new StreamForwardingBindingEntry[state.State.BindingsByTarget.Count];
        var index = 0;
        foreach (var entry in state.State.BindingsByTarget.Values)
            snapshot[index++] = CloneEntry(entry);

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
