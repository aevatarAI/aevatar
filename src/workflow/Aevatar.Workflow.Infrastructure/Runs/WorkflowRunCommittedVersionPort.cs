using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.Runs;

// 06-20-observatory-run-state-feed (R2a): the reclaim gate needs the run's terminal/head committed
// StateVersion, sourced from the run actor's authoritative event-store head while the actor is still alive
// at teardown — NOT from the current-state readmodel (which is the very doc that does not exist yet for an
// ad-hoc /api/chat run). IEventStore.GetVersionAsync returns the committed head version; commits publish
// StateEvent.Version, which is the same version space the durable materialization watermark advances
// against, so the gate's "watermark >= head version" comparison is meaningful.
//
// 06-20-observatory-run-state-feed (R2, codex DIFF review §10 C6): IEventStore is OPTIONAL here. The adapter
// is always registered (so the reclaim gate always resolves on every workflow host), but a host that does
// not wire an event store has no committed head to read → the head version is unknown → the gate defers and
// never destroys on unconfirmed materialization. The adapter must DEFER, not crash, when the event store is
// absent, mirroring WorkflowRunMaterializationWatermarkPort's optional watermark-query-port handling.
internal sealed class WorkflowRunCommittedVersionPort : IWorkflowRunCommittedVersionPort
{
    private readonly IEventStore? _eventStore;
    private readonly ILogger<WorkflowRunCommittedVersionPort>? _logger;

    public WorkflowRunCommittedVersionPort(
        IEventStore? eventStore = null,
        ILogger<WorkflowRunCommittedVersionPort>? logger = null)
    {
        _eventStore = eventStore;
        _logger = logger;
    }

    public async Task<long?> GetCommittedVersionAsync(string runActorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runActorId) || _eventStore == null)
            return null;

        try
        {
            return await _eventStore.GetVersionAsync(runActorId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The reclaim gate treats an unknown head version as "defer, never destroy"; a transient
            // event-store read failure must not destroy the actor on unconfirmed materialization.
            _logger?.LogWarning(
                ex,
                "Failed to read committed head version for workflow run actor {RunActorId}; reclaim will defer.",
                runActorId);
            return null;
        }
    }
}
