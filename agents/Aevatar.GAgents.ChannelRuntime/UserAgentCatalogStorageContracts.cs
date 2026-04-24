namespace Aevatar.GAgents.ChannelRuntime;

internal static class UserAgentCatalogStorageContracts
{
    // Keep actor and document-store identifiers stable across the rename so Orleans state
    // and read models remain reachable. Use a dedicated durable projection scope identity
    // to avoid colliding with the legacy AgentRegistry materialization scope actor type.
    public const string StoreActorId = "agent-registry-store";
    public const string ReadModelIndexName = "agent-registry";
    public const string LegacyDurableProjectionKind = "agent-registry";
    public const string DurableProjectionKind = "user-agent-catalog-read-model";
}
