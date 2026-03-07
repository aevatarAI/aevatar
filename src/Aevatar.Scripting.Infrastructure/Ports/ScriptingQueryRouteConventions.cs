using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Infrastructure.Ports;

internal static class ScriptingQueryRouteConventions
{
    public static string BuildDefinitionSnapshotTimeoutMessage(string requestId) =>
        $"Timeout waiting for script definition snapshot query response. request_id={requestId}";

    public static string BuildDefinitionMutationTimeoutMessage(string requestId) =>
        $"Timeout waiting for script definition mutation command response. request_id={requestId}";

    public static string BuildCatalogEntryTimeoutMessage(string requestId) =>
        $"Timeout waiting for script catalog entry query response. request_id={requestId}";

    public static string BuildCatalogMutationTimeoutMessage(string requestId) =>
        $"Timeout waiting for script catalog mutation command response. request_id={requestId}";

    public static string BuildEvolutionCommandAckTimeoutMessage(string requestId) =>
        $"Timeout waiting for script evolution command ack response. request_id={requestId}";

    public static string BuildEvolutionSnapshotTimeoutMessage(string requestId) =>
        $"Timeout waiting for script evolution snapshot query response. request_id={requestId}";

    public static string BuildRuntimeSnapshotTimeoutMessage(string requestId) =>
        $"Timeout waiting for script runtime snapshot query response. request_id={requestId}";

    public const string DefinitionReplyStreamPrefix = ScriptingQueryChannels.DefinitionReplyStreamPrefix;
    public const string CatalogReplyStreamPrefix = ScriptingQueryChannels.CatalogReplyStreamPrefix;
    public const string EvolutionReplyStreamPrefix = ScriptingQueryChannels.EvolutionReplyStreamPrefix;
    public const string RuntimeReplyStreamPrefix = ScriptingQueryChannels.RuntimeReplyStreamPrefix;
}
