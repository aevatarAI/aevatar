namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

public sealed class ElasticsearchProjectionRelationStore
    : IProjectionRelationStore,
      IProjectionStoreProviderMetadata
{
    public ElasticsearchProjectionRelationStore(
        string providerName = ProjectionReadModelProviderNames.Elasticsearch)
    {
        ProviderCapabilities = new ProjectionReadModelProviderCapabilities(
            providerName,
            supportsIndexing: true,
            indexKinds: [ProjectionReadModelIndexKind.Document],
            supportsAliases: false,
            supportsSchemaValidation: false,
            supportsRelations: false,
            supportsRelationTraversal: false);
    }

    public ProjectionReadModelProviderCapabilities ProviderCapabilities { get; }

    public Task UpsertNodeAsync(ProjectionRelationNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task UpsertEdgeAsync(ProjectionRelationEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectionRelationEdge>> GetNeighborsAsync(
        ProjectionRelationQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProjectionRelationEdge>>([]);
    }

    public Task<ProjectionRelationSubgraph> GetSubgraphAsync(
        ProjectionRelationQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ProjectionRelationSubgraph());
    }
}
