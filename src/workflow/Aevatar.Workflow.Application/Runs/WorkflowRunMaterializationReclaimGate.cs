using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Application.Runs;

// 06-20-observatory-run-state-feed (R2): gates the destroy of the throwaway actors an ad-hoc /api/chat run
// creates on CONFIRMED durable materialization of the run's current-state doc. An ad-hoc run actor was
// previously destroyed on completion while its durable ExecutionMaterialization scope drained
// asynchronously, so the doc was dropped before it materialized and the run became invisible to the
// observatory. The gate reads the run's committed head version from the run actor / event-store head (NOT
// the readmodel that does not exist yet), then waits for the durable materialization watermark to reach
// that version before allowing the destroy. On timeout / scope absent / unknown head version it DEFERS
// (never destroys on unconfirmed materialization — that is the silent doc loss this fix removes); deferred
// stragglers persist like deployed runs and are documented debt pending a uniform run-actor retention/GC
// follow-up (none exists today; per-call ids built at WorkflowRunActorPort BuildRunActorId).
internal sealed class WorkflowRunMaterializationReclaimGate
{
    private readonly IWorkflowRunCommittedVersionPort _committedVersionPort;
    private readonly IWorkflowRunMaterializationWatermarkPort _watermarkPort;
    private readonly WorkflowRunReclaimOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly ILogger? _logger;

    public WorkflowRunMaterializationReclaimGate(
        IWorkflowRunCommittedVersionPort committedVersionPort,
        IWorkflowRunMaterializationWatermarkPort watermarkPort,
        WorkflowRunReclaimOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ILogger? logger = null)
    {
        _committedVersionPort = committedVersionPort ?? throw new ArgumentNullException(nameof(committedVersionPort));
        _watermarkPort = watermarkPort ?? throw new ArgumentNullException(nameof(watermarkPort));
        _options = options ?? new WorkflowRunReclaimOptions();
        _delayAsync = delayAsync ?? ((delay, ct) => Task.Delay(delay, ct));
        _logger = logger;
    }

    /// <summary>
    /// Awaits confirmed durable materialization of <paramref name="runActorId"/> up to its committed head
    /// version. Returns <c>true</c> only when materialization is confirmed (safe to destroy); returns
    /// <c>false</c> to DEFER (do not destroy) on unknown head version, watermark-unreached, or scope absent.
    /// Uses the supplied <paramref name="ct"/> which MUST be a teardown-lifetime token (not the request
    /// token) so request cancellation does not abort the gate before it confirms.
    /// </summary>
    public async Task<bool> TryConfirmMaterializedAsync(string runActorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runActorId))
            return Defer(WorkflowRunReclaimMetrics.ReasonUnknownHeadVersion, runActorId, headVersion: null);

        var headVersion = await _committedVersionPort.GetCommittedVersionAsync(runActorId, ct);
        if (headVersion is not > 0)
        {
            // No committed head (or unreadable): nothing durable to wait for / cannot prove materialization.
            return Defer(WorkflowRunReclaimMetrics.ReasonUnknownHeadVersion, runActorId, headVersion);
        }

        var maxPolls = Math.Max(1, _options.MaxWatermarkPolls);
        for (var attempt = 0; attempt < maxPolls; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var materializedVersion = await _watermarkPort.GetMaterializedVersionAsync(runActorId, ct);
            if (materializedVersion is { } version && version >= headVersion.Value)
            {
                WorkflowRunReclaimMetrics.RecordConfirmed();
                return true;
            }

            if (attempt + 1 < maxPolls)
                await _delayAsync(_options.WatermarkPollInterval, ct);
        }

        return Defer(WorkflowRunReclaimMetrics.ReasonWatermarkUnreached, runActorId, headVersion);
    }

    private bool Defer(string reason, string runActorId, long? headVersion)
    {
        WorkflowRunReclaimMetrics.RecordDeferred(reason);
        _logger?.LogWarning(
            "Workflow ad-hoc run reclaim deferred for actor {RunActorId} (reason={Reason}, headVersion={HeadVersion}); " +
            "throwaway run/definition actors are left persisted to avoid silent current-state doc loss.",
            runActorId,
            reason,
            headVersion);
        return false;
    }
}
