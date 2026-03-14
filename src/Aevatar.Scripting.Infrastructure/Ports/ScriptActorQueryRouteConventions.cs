namespace Aevatar.Scripting.Infrastructure.Ports;

internal static class ScriptActorQueryRouteConventions
{
    public const string DefinitionSnapshotReplyStreamPrefix = "scripting.query.definition.snapshot";
    public const string CatalogEntryReplyStreamPrefix = "scripting.query.catalog.entry";
    public const string BindingReplyStreamPrefix = "scripting.query.runtime.binding";

    public static string BuildDefinitionTimeoutMessage(string requestId) =>
        $"Timeout waiting for script definition snapshot query response. request_id={requestId}";

    public static string BuildCatalogTimeoutMessage(string requestId) =>
        $"Timeout waiting for script catalog entry query response. request_id={requestId}";

    public static string BuildBindingTimeoutMessage(string requestId) =>
        $"Timeout waiting for script runtime binding query response. request_id={requestId}";
}
