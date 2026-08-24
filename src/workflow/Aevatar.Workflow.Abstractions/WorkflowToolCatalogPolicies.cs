namespace Aevatar.Workflow.Abstractions;

/// <summary>
/// Stable workflow-owned policy identities. An empty version is reserved for already-committed
/// v0 runs; every newly bound definition and run uses <see cref="CurrentVersion"/>.
/// </summary>
public static class WorkflowToolCatalogPolicies
{
    public const string LegacyV0 = "";

    public const string CurrentVersion = "workflow-agent-turn-tool-catalog/v1";

    // Durable optimization target only. A valid exact catalog may exceed it without rejection.
    public const int MaximumWorkflowToolCount = 16;

    public const int MaximumWorkflowSchemaBytes = 128 * 1024;

    public static bool IsCurrent(string? policyVersion) =>
        string.Equals(policyVersion?.Trim(), CurrentVersion, StringComparison.Ordinal);
}
