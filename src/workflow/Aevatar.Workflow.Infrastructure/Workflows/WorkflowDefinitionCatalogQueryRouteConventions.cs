namespace Aevatar.Workflow.Infrastructure.Workflows;

internal static class WorkflowDefinitionCatalogQueryRouteConventions
{
    public const string DefinitionReplyStreamPrefix = "workflow.definition.catalog.reply";
    public const string NamesReplyStreamPrefix = "workflow.definition.catalog.names.reply";

    public static string BuildDefinitionTimeoutMessage(string requestId) =>
        $"Workflow definition query timed out. request_id={requestId}";

    public static string BuildNamesTimeoutMessage(string requestId) =>
        $"Workflow definition names query timed out. request_id={requestId}";
}
