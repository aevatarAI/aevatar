namespace Aevatar.GAgents.ChannelRuntime;

internal static class UserAgentCatalogStorageContracts
{
    // Keep durable identifiers stable across the rename so Orleans state and read models remain reachable.
    public const string StoreActorId = "agent-registry-store";
    public const string ProjectionKind = "agent-registry";
    public const string ReadModelIndexName = "agent-registry";
}
