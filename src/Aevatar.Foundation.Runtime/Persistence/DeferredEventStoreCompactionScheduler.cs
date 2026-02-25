using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Persistence;

/// <summary>
/// Runtime-level scheduler that defers event-store compaction until explicit idle trigger.
/// </summary>
public sealed class DeferredEventStoreCompactionScheduler : IEventStoreCompactionScheduler
{
    private readonly IEventStore _eventStore;
    private readonly ILogger<DeferredEventStoreCompactionScheduler> _logger;
    private readonly ConcurrentDictionary<string, long> _pendingByAgent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentLocks = new(StringComparer.Ordinal);

    public DeferredEventStoreCompactionScheduler(
        IEventStore eventStore,
        ILogger<DeferredEventStoreCompactionScheduler>? logger = null)
    {
        _eventStore = eventStore;
        _logger = logger ?? NullLogger<DeferredEventStoreCompactionScheduler>.Instance;
    }

    public Task ScheduleAsync(string agentId, long compactToVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();
        if (compactToVersion <= 0)
            return Task.CompletedTask;

        _pendingByAgent.AddOrUpdate(
            agentId,
            compactToVersion,
            (_, current) => Math.Max(current, compactToVersion));
        return Task.CompletedTask;
    }

    public async Task RunOnIdleAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        var gate = _agentLocks.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!_pendingByAgent.TryRemove(agentId, out var compactToVersion) || compactToVersion <= 0)
                return;

            try
            {
                var deleted = await _eventStore.DeleteEventsUpToAsync(agentId, compactToVersion, ct);
                _logger.LogInformation(
                    "Event store compaction executed on idle. agentId={AgentId} compactToVersion={CompactToVersion} deletedEvents={DeletedEvents} result={Result}",
                    agentId,
                    compactToVersion,
                    deleted,
                    "ok");
            }
            catch (Exception ex)
            {
                // Requeue with latest target for best-effort retry in next idle window.
                _pendingByAgent.AddOrUpdate(
                    agentId,
                    compactToVersion,
                    (_, current) => Math.Max(current, compactToVersion));
                _logger.LogWarning(
                    ex,
                    "Event store compaction on idle failed and was re-queued. agentId={AgentId} compactToVersion={CompactToVersion} result={Result} errorType={ErrorType}",
                    agentId,
                    compactToVersion,
                    "failed",
                    ex.GetType().Name);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
