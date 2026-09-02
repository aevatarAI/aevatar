using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal sealed class ElasticsearchProjectionDocumentRepairStore<TReadModel, TKey>
    : IElasticsearchProjectionDocumentRepairStore<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    private readonly ElasticsearchProjectionDocumentStore<TReadModel, TKey> _store;

    public ElasticsearchProjectionDocumentRepairStore(
        ElasticsearchProjectionDocumentStore<TReadModel, TKey> store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task<ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey>?> InspectAsync(
        TKey key,
        CancellationToken ct = default) =>
        _store.InspectRepairAsync(key, ct);

    public async Task<ElasticsearchProjectionDocumentRepairDeleteDisposition> DeleteIfUnchangedAsync(
        ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey> lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        try
        {
            using var deleteCancellation = new CancellationTokenSource(_store.RepairRequestTimeout);
            return await _store.DeleteRepairIfUnchangedCoreAsync(
                lease,
                deleteCancellation.Token);
        }
        catch (Exception ex) when (IsAmbiguousDeleteFailure(ex))
        {
            using var inspectionCancellation =
                new CancellationTokenSource(_store.RepairRequestTimeout);
            var currentLease = await _store.InspectRepairLeaseRevisionAsync(
                lease,
                inspectionCancellation.Token);
            if (currentLease is null)
                return ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent;

            throw;
        }
    }

    private static bool IsAmbiguousDeleteFailure(Exception exception) =>
        exception is HttpRequestException or OperationCanceledException or TimeoutException;
}
