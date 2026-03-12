using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.Foundation.Runtime.Streaming;

internal interface IStreamForwardingBindingSource
{
    IEnumerable<StreamForwardingBinding> GetBindings(string sourceStreamId);

    void RemoveByActor(string actorId);
}

public sealed class InMemoryStreamForwardingRegistry : IStreamForwardingRegistry, IStreamForwardingBindingSource
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, StreamForwardingBinding>> _bindingsBySource =
        new(StringComparer.Ordinal);

    public Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.SourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.TargetStreamId);
        ct.ThrowIfCancellationRequested();

        var byTarget = _bindingsBySource.GetOrAdd(
            binding.SourceStreamId,
            _ => new ConcurrentDictionary<string, StreamForwardingBinding>(StringComparer.Ordinal));
        byTarget[binding.TargetStreamId] = CloneBinding(binding);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);
        ct.ThrowIfCancellationRequested();

        if (_bindingsBySource.TryGetValue(sourceStreamId, out var byTarget))
        {
            byTarget.TryRemove(targetStreamId, out _);
            if (byTarget.IsEmpty)
                _bindingsBySource.TryRemove(sourceStreamId, out _);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(string sourceStreamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ct.ThrowIfCancellationRequested();

        if (!_bindingsBySource.TryGetValue(sourceStreamId, out var byTarget))
            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);

        return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(
            byTarget.Values.Select(CloneBinding).ToList());
    }

    public IEnumerable<StreamForwardingBinding> GetBindings(string sourceStreamId)
    {
        if (_bindingsBySource.TryGetValue(sourceStreamId, out var byTarget) && !byTarget.IsEmpty)
            return byTarget.Values;

        return [];
    }

    public void RemoveByActor(string actorId)
    {
        _bindingsBySource.TryRemove(actorId, out _);
        foreach (var byTarget in _bindingsBySource.Values)
            byTarget.TryRemove(actorId, out _);
    }

    private static StreamForwardingBinding CloneBinding(StreamForwardingBinding binding) =>
        new()
        {
            SourceStreamId = binding.SourceStreamId,
            TargetStreamId = binding.TargetStreamId,
            ForwardingMode = binding.ForwardingMode,
            DirectionFilter = new HashSet<BroadcastDirection>(binding.DirectionFilter),
            EventTypeFilter = new HashSet<string>(binding.EventTypeFilter, StringComparer.Ordinal),
            Version = binding.Version,
            LeaseId = binding.LeaseId,
        };
}
