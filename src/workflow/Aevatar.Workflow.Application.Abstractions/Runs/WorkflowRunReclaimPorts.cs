namespace Aevatar.Workflow.Application.Abstractions.Runs;

// 06-20-observatory-run-state-feed (R2): an ad-hoc /api/chat run actor is destroyed on completion while
// its durable ExecutionMaterialization scope drains asynchronously, so the current-state doc is dropped
// before it materializes. The reclaim path gates the destroy on confirmed durable materialization. These
// two narrow read ports are the authoritative inputs to that gate (design.md §8 R2a/R2b):
//   - the run's committed head version comes from the run actor / event-store head (NOT the readmodel,
//     which is the very doc that does not exist yet);
//   - the durable materialization watermark comes from the projection-scope status readmodel.

/// <summary>
/// Reads a workflow run actor's committed head version from its authoritative event-store head.
/// Used by the reclaim gate to learn the terminal/head version to wait for before destroying the actor.
/// This is a teardown/lifecycle read, never a query-path read.
/// </summary>
public interface IWorkflowRunCommittedVersionPort
{
    /// <summary>
    /// Returns the run actor's current committed head version, or <c>null</c> when the version is unknown
    /// (no committed events / store unreachable). A <c>null</c> result means the reclaim gate must defer.
    /// </summary>
    Task<long?> GetCommittedVersionAsync(string runActorId, CancellationToken ct = default);
}

/// <summary>
/// Reads the durable ExecutionMaterialization projection scope watermark for a workflow run actor — the
/// highest committed version that has been materialized into the current-state readmodel for that run.
/// </summary>
public interface IWorkflowRunMaterializationWatermarkPort
{
    /// <summary>
    /// Returns the last successfully materialized version for the run's durable execution-materialization
    /// scope, or <c>null</c> when the materialization/status scope is absent or unreadable. A <c>null</c>
    /// result means the reclaim gate must defer (never destroy on unconfirmed materialization).
    /// </summary>
    Task<long?> GetMaterializedVersionAsync(string runActorId, CancellationToken ct = default);
}
