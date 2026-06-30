using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Orchestration;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.Runs;

// 06-20-observatory-run-state-feed (R2b): reads the DURABLE ExecutionMaterialization scope watermark for a
// run actor so the reclaim gate can confirm the current-state doc has materialized up to the run's head
// version before destroying the throwaway actors. The scope key is exact:
//   rootActorId = run actor id, projectionKind = workflow-execution-materialization,
//   mode = DurableMaterialization, sessionId = "" (NOT the ExecutionSession session-scope watermark).
// The underlying ProjectionScopeStatusQueryPort returns the watermark only when the status scope doc is
// Active && !Released, and the watermark advances against the source actor's committed StateEvent.Version
// (same version space as IWorkflowRunCommittedVersionPort).
//
// IProjectionScopeWatermarkQueryPort is registered by AddProjectionScopeStatusRuntimeCore, which the
// channel runtime (and thus the mainnet host) wires; the standalone workflow host does not. So it is an
// OPTIONAL dependency: when absent, the watermark is unknown → the reclaim gate defers (never destroys on
// unconfirmed materialization). This is the same "scope absent → defer" behavior required by R2b.
internal sealed class WorkflowRunMaterializationWatermarkPort : IWorkflowRunMaterializationWatermarkPort
{
    private readonly IProjectionScopeWatermarkQueryPort? _watermarkQueryPort;
    private readonly ILogger<WorkflowRunMaterializationWatermarkPort>? _logger;

    public WorkflowRunMaterializationWatermarkPort(
        IProjectionScopeWatermarkQueryPort? watermarkQueryPort = null,
        ILogger<WorkflowRunMaterializationWatermarkPort>? logger = null)
    {
        _watermarkQueryPort = watermarkQueryPort;
        _logger = logger;
    }

    public async Task<long?> GetMaterializedVersionAsync(string runActorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runActorId) || _watermarkQueryPort == null)
            return null;

        var scopeKey = new ProjectionRuntimeScopeKey(
            runActorId,
            WorkflowProjectionKinds.ExecutionMaterialization,
            ProjectionRuntimeMode.DurableMaterialization,
            SessionId: string.Empty);

        try
        {
            return await _watermarkQueryPort.GetLastSuccessfulVersionAsync(scopeKey, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Treat an unreadable watermark as "scope absent" → defer; never destroy on unconfirmed
            // materialization.
            _logger?.LogWarning(
                ex,
                "Failed to read durable materialization watermark for workflow run actor {RunActorId}; reclaim will defer.",
                runActorId);
            return null;
        }
    }
}
