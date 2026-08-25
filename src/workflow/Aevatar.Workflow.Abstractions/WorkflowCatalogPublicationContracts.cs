namespace Aevatar.Workflow.Abstractions;

/// <summary>
/// Stable public workflow catalog materialization contract identities.
/// </summary>
public static class WorkflowCatalogPublicationContracts
{
    public const string LegacyV0 = "";

    public const string CurrentVersion = "workflow-catalog-publication/v1";

    public static bool IsCurrent(string? contractVersion) =>
        string.Equals(contractVersion?.Trim(), CurrentVersion, StringComparison.Ordinal);
}
