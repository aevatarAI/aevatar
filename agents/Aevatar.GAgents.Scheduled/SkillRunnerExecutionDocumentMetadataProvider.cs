using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Scheduled;

public sealed class SkillRunnerExecutionDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<SkillRunnerExecutionDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: UserAgentCatalogStorageContracts.RunnerExecutionReadModelIndexName,
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
