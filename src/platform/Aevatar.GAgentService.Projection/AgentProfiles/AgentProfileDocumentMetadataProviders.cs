using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Projection.AgentProfiles;

public sealed class AgentProfileNamespaceCatalogDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<AgentProfileNamespaceCatalogDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "agent-profile-namespaces",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}

public sealed class AgentProfileOwnerDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<AgentProfileOwnerDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "agent-profile-management",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}

public sealed class AgentProfileExecutionDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<AgentProfileExecutionDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "agent-profile-execution",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
