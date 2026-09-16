namespace Aevatar.Workflow.Core.Execution;

/// <summary>
/// Which evidence a normalized completion ledger keeps for exact replay validation.
/// The kernel derives it from the runtime-owned schema context: only an actor that adopted
/// workflow state schema v2 (value lifecycle) may replace raw payload retention with digests.
/// </summary>
internal enum WorkflowValueReplayEvidence
{
    /// <summary>Keep the raw canonical value alive for the replay ledger (schema v1 readers).</summary>
    RawValue = 0,

    /// <summary>Record SHA-256 + size evidence and let unreferenced raw payloads be pruned (schema v2).</summary>
    Digest = 1,
}
