namespace Aevatar.GAgentService.Abstractions.ScopeWorkflows;

public static class ScopeWorkflowCatalogueActorIds
{
    public const string DraftSourceKind = "draft";
    public const string ServiceSourceKind = "service";

    public static string Row(string scopeId, string workflowId) =>
        $"scope-workflow-catalogue-row:{scopeId}:{workflowId}";

    public static string RowDocument(string scopeId, string workflowId) =>
        $"{scopeId}:workflow:{workflowId}";

    public static string SourceDocument(string scopeId, string workflowId, string sourceKind) =>
        $"{scopeId}:{workflowId}:{sourceKind}";

    public static string SourceActor(string scopeId, string workflowId, string sourceKind) =>
        $"scope-workflow-catalogue-source:{scopeId}:{workflowId}:{sourceKind}";
}
