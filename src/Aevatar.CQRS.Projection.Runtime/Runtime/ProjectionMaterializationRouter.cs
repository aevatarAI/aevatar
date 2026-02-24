using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionMaterializationRouter<TReadModel, TKey>
    : IProjectionMaterializationRouter<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IDocumentProjectionStore<TReadModel, TKey>? _documentStore;
    private readonly IProjectionGraphMaterializer<TReadModel>? _graphMaterializer;
    private readonly ILogger<ProjectionMaterializationRouter<TReadModel, TKey>> _logger;
    private readonly bool _requiresDocumentStore = typeof(IDocumentReadModel).IsAssignableFrom(typeof(TReadModel));
    private readonly bool _requiresGraphStore = typeof(IGraphReadModel).IsAssignableFrom(typeof(TReadModel));

    public ProjectionMaterializationRouter(
        IDocumentProjectionStore<TReadModel, TKey>? documentStore = null,
        IProjectionGraphMaterializer<TReadModel>? graphMaterializer = null,
        ILogger<ProjectionMaterializationRouter<TReadModel, TKey>>? logger = null)
    {
        _documentStore = documentStore;
        _graphMaterializer = graphMaterializer;
        _logger = logger ?? NullLogger<ProjectionMaterializationRouter<TReadModel, TKey>>.Instance;
    }

    public async Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        EnsureStoresReady();
        if (_requiresDocumentStore)
            await _documentStore!.UpsertAsync(readModel, ct);

        if (_requiresGraphStore)
            await _graphMaterializer!.UpsertGraphAsync(readModel, ct);
    }

    public async Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ct.ThrowIfCancellationRequested();

        EnsureStoresReady();
        if (_documentStore == null)
            throw new InvalidOperationException(
                $"Projection materialization mutate requires a document store for read model '{typeof(TReadModel).FullName}'.");

        await _documentStore.MutateAsync(key, mutate, ct);
        if (!_requiresGraphStore)
            return;

        var updated = await _documentStore.GetAsync(key, ct);
        if (updated == null)
        {
            _logger.LogWarning(
                "Projection materialization graph refresh skipped because the document snapshot is missing. readModelType={ReadModelType}",
                typeof(TReadModel).FullName);
            return;
        }

        await _graphMaterializer!.UpsertGraphAsync(updated, ct);
    }

    public Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        if (_documentStore == null)
        {
            throw new InvalidOperationException(
                $"Projection materialization query requires a document store for read model '{typeof(TReadModel).FullName}'.");
        }

        return _documentStore.GetAsync(key, ct);
    }

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        if (_documentStore == null)
        {
            throw new InvalidOperationException(
                $"Projection materialization query requires a document store for read model '{typeof(TReadModel).FullName}'.");
        }

        return _documentStore.ListAsync(take, ct);
    }

    private void EnsureStoresReady()
    {
        if (_requiresDocumentStore && _documentStore == null)
        {
            throw new InvalidOperationException(
                $"Document capability is required by read model '{typeof(TReadModel).FullName}', but no document projection store is registered.");
        }

        if (_requiresGraphStore && _graphMaterializer == null)
        {
            throw new InvalidOperationException(
                $"Graph capability is required by read model '{typeof(TReadModel).FullName}', but no graph projection materializer is registered.");
        }
    }
}
