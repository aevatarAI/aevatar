using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Metadata;

public sealed class AgentProfileCatalogReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<AgentProfileCatalogReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "gagent-service-agent-profile-catalog",
        new Dictionary<string, object?>(),
        new Dictionary<string, object?>(),
        new Dictionary<string, object?>());
}

public sealed class AgentProfileManagementReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<AgentProfileManagementReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "gagent-service-agent-profile-management",
        new Dictionary<string, object?>(),
        new Dictionary<string, object?>(),
        new Dictionary<string, object?>());
}

public sealed class AgentProfileExecutionReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<AgentProfileExecutionReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "gagent-service-agent-profile-execution",
        new Dictionary<string, object?>(),
        new Dictionary<string, object?>(),
        new Dictionary<string, object?>());
}
