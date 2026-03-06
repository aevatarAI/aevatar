using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Infrastructure.Ports;

internal static class ScriptingQueryRouteConventions
{
    public static string BuildDefinitionSnapshotTimeoutMessage(string requestId) =>
        $"Timeout waiting for script definition snapshot query response. request_id={requestId}";

    public static string BuildCatalogEntryTimeoutMessage(string requestId) =>
        $"Timeout waiting for script catalog entry query response. request_id={requestId}";

    public static string BuildEvolutionDecisionTimeoutMessage(string requestId) =>
        $"Timeout waiting for script evolution decision query response. request_id={requestId}";

    public const string DefinitionReplyStreamPrefix = ScriptingQueryChannels.DefinitionReplyStreamPrefix;
    public const string CatalogReplyStreamPrefix = ScriptingQueryChannels.CatalogReplyStreamPrefix;
    public const string EvolutionReplyStreamPrefix = ScriptingQueryChannels.EvolutionReplyStreamPrefix;
}
