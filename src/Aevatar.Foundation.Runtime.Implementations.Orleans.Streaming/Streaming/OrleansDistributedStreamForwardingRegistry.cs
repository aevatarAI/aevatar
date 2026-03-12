using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;
using System.Collections.Concurrent;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class OrleansDistributedStreamForwardingRegistry : IStreamForwardingRegistry
{
    private static readonly TimeSpan DefaultRevisionCheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly IGrainFactory _grainFactory;
    private readonly TimeSpan _revisionCheckInterval;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public OrleansDistributedStreamForwardingRegistry(
        IGrainFactory grainFactory,
        TimeSpan? revisionCheckInterval = null)
    {
        _grainFactory = grainFactory;
        _revisionCheckInterval = revisionCheckInterval ?? DefaultRevisionCheckInterval;
    }

    public async Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.SourceStreamId);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IStreamTopologyGrain>(binding.SourceStreamId);
        await grain.UpsertAsync(ToEntry(binding));
        _cache.TryRemove(binding.SourceStreamId, out _);
    }

    public async Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IStreamTopologyGrain>(sourceStreamId);
        await grain.RemoveAsync(targetStreamId);
        _cache.TryRemove(sourceStreamId, out _);
    }

    public async Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(string sourceStreamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ct.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(sourceStreamId, out var cached) &&
            cached.NextRevisionCheckUtc > now)
        {
            return cached.Bindings;
        }

        var grain = _grainFactory.GetGrain<IStreamTopologyGrain>(sourceStreamId);
        var revision = await grain.GetRevisionAsync();
        if (cached != null && cached.Revision == revision)
        {
            _cache[sourceStreamId] = new CacheEntry(cached.Bindings, revision, ComputeNextRevisionCheckUtc(now));
            return cached.Bindings;
        }

        var entries = await grain.ListAsync();
        var clonedBindings = entries.Select(ToBinding).Select(CloneBinding).ToArray();
        _cache[sourceStreamId] = new CacheEntry(clonedBindings, revision, ComputeNextRevisionCheckUtc(now));
        return clonedBindings;
    }

    private DateTime ComputeNextRevisionCheckUtc(DateTime now)
    {
        if (_revisionCheckInterval <= TimeSpan.Zero)
            return now;

        return now + _revisionCheckInterval;
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
            DirectionFilter = new HashSet<BroadcastDirection>(entry.DirectionFilter),
            EventTypeFilter = new HashSet<string>(entry.EventTypeFilter, StringComparer.Ordinal),
            Version = entry.Version,
            LeaseId = entry.LeaseId,
        };

    private sealed record CacheEntry(
        IReadOnlyList<StreamForwardingBinding> Bindings,
        long Revision,
        DateTime NextRevisionCheckUtc);
}
