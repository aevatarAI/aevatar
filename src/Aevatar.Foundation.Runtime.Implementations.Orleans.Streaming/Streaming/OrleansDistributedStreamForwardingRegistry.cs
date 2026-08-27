using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using System.Collections.Concurrent;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

public sealed class OrleansDistributedStreamForwardingRegistry
    : IStreamForwardingRegistry,
      IStreamForwardingBindingAuthority
{
    private const int DefaultTopologyAttemptLimit = 30;
    private static readonly TimeSpan DefaultRevisionCheckInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultTopologyRetryDelay = TimeSpan.FromSeconds(1);

    private readonly IGrainFactory _grainFactory;
    private readonly TimeSpan _revisionCheckInterval;
    private readonly ILogger<OrleansDistributedStreamForwardingRegistry> _logger;
    private readonly int _topologyAttemptLimit;
    private readonly TimeSpan _topologyRetryDelay;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public OrleansDistributedStreamForwardingRegistry(
        IGrainFactory grainFactory,
        TimeSpan? revisionCheckInterval = null,
        ILogger<OrleansDistributedStreamForwardingRegistry>? logger = null)
        : this(
            grainFactory,
            revisionCheckInterval ?? DefaultRevisionCheckInterval,
            logger,
            DefaultTopologyAttemptLimit,
            DefaultTopologyRetryDelay)
    {
    }

    internal OrleansDistributedStreamForwardingRegistry(
        IGrainFactory grainFactory,
        TimeSpan revisionCheckInterval,
        ILogger<OrleansDistributedStreamForwardingRegistry>? logger,
        int topologyAttemptLimit,
        TimeSpan topologyRetryDelay)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(topologyAttemptLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(topologyRetryDelay, TimeSpan.Zero);

        _grainFactory = grainFactory;
        _revisionCheckInterval = revisionCheckInterval;
        _logger = logger ?? NullLogger<OrleansDistributedStreamForwardingRegistry>.Instance;
        _topologyAttemptLimit = topologyAttemptLimit;
        _topologyRetryDelay = topologyRetryDelay;
    }

    public async Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.SourceStreamId);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IStreamTopologyGrain>(binding.SourceStreamId);
        var entry = ToEntry(binding);
        await ExecuteTopologyCallAsync(
            () => grain.UpsertAsync(entry),
            "upsert",
            binding.SourceStreamId,
            ct);
        _cache.TryRemove(binding.SourceStreamId, out _);
    }

    public async Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IStreamTopologyGrain>(sourceStreamId);
        await ExecuteTopologyCallAsync(
            () => grain.RemoveAsync(targetStreamId),
            "remove",
            sourceStreamId,
            ct);
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
        var revision = await ExecuteTopologyCallAsync(
            grain.GetRevisionAsync,
            "get-revision",
            sourceStreamId,
            ct);
        if (cached != null && cached.Revision == revision)
        {
            _cache[sourceStreamId] = new CacheEntry(cached.Bindings, revision, ComputeNextRevisionCheckUtc(now));
            return cached.Bindings;
        }

        var entries = await ExecuteTopologyCallAsync(
            grain.ListAsync,
            "list",
            sourceStreamId,
            ct);
        var clonedBindings = entries.Select(ToBinding).Select(CloneBinding).ToArray();
        _cache[sourceStreamId] = new CacheEntry(clonedBindings, revision, ComputeNextRevisionCheckUtc(now));
        return clonedBindings;
    }

    public async Task<StreamForwardingBinding?> GetAsync(
        string sourceStreamId,
        string targetStreamId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IStreamTopologyGrain>(sourceStreamId);
        // Keep the Orleans grain RPC surface compatible with rolling peers. The authority
        // bypasses this registry's process-local cache by reading the grain snapshot directly.
        var entries = await ExecuteTopologyCallAsync(
            grain.ListAsync,
            "authoritative-list",
            sourceStreamId,
            ct);
        var entry = entries.SingleOrDefault(candidate =>
            string.Equals(candidate.TargetStreamId, targetStreamId, StringComparison.Ordinal));
        return entry == null ? null : CloneBinding(ToBinding(entry));
    }

    private Task ExecuteTopologyCallAsync(
        Func<Task> call,
        string operation,
        string sourceStreamId,
        CancellationToken ct) =>
        ExecuteTopologyCallAsync(
            async () =>
            {
                await call().WaitAsync(ct);
                return true;
            },
            operation,
            sourceStreamId,
            ct);

    private async Task<TResult> ExecuteTopologyCallAsync<TResult>(
        Func<Task<TResult>> call,
        string operation,
        string sourceStreamId,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _topologyAttemptLimit; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await call().WaitAsync(ct);
            }
            catch (Exception ex) when (
                attempt < _topologyAttemptLimit &&
                IsTopologyConvergenceFailure(ex))
            {
                if (attempt == 1 || attempt % 5 == 0)
                {
                    _logger.LogWarning(
                        ex,
                        "Orleans stream topology call was rejected during topology convergence. " +
                        "operation={Operation} sourceStreamId={SourceStreamId} attempt={Attempt}/{AttemptLimit}",
                        operation,
                        sourceStreamId,
                        attempt,
                        _topologyAttemptLimit);
                }

                await Task.Delay(_topologyRetryDelay, ct);
            }
        }

        throw new InvalidOperationException("Orleans stream topology retry loop exited unexpectedly.");
    }

    private static bool IsTopologyConvergenceFailure(Exception exception) =>
        exception switch
        {
            OrleansMessageRejectionException => true,
            OrleansException orleansException when
                orleansException.Message.Contains(
                    "is not stable to perform the lookup",
                    StringComparison.Ordinal) &&
                orleansException.Message.Contains("Retry later", StringComparison.Ordinal) => true,
            AggregateException aggregate when aggregate.InnerExceptions.Count > 0 =>
                aggregate.InnerExceptions.All(IsTopologyConvergenceFailure),
            _ when exception.InnerException is not null =>
                IsTopologyConvergenceFailure(exception.InnerException),
            _ => false,
        };

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
            DirectionFilter = new HashSet<TopologyAudience>(binding.DirectionFilter),
            EventTypeFilter = new HashSet<string>(binding.EventTypeFilter, StringComparer.Ordinal),
            Version = binding.Version,
            LeaseId = binding.LeaseId,
            TargetActorKind = binding.TargetActorKind,
            ActivationGeneration = binding.ActivationGeneration,
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
            TargetActorKind = binding.TargetActorKind,
            ActivationGeneration = binding.ActivationGeneration,
        };

    private static StreamForwardingBinding ToBinding(StreamForwardingBindingEntry entry) =>
        new()
        {
            SourceStreamId = entry.SourceStreamId,
            TargetStreamId = entry.TargetStreamId,
            ForwardingMode = entry.ForwardingMode,
            DirectionFilter = new HashSet<TopologyAudience>(entry.DirectionFilter),
            EventTypeFilter = new HashSet<string>(entry.EventTypeFilter, StringComparer.Ordinal),
            Version = entry.Version,
            LeaseId = entry.LeaseId,
            TargetActorKind = entry.TargetActorKind,
            ActivationGeneration = entry.ActivationGeneration,
        };

    private sealed record CacheEntry(
        IReadOnlyList<StreamForwardingBinding> Bindings,
        long Revision,
        DateTime NextRevisionCheckUtc);
}
