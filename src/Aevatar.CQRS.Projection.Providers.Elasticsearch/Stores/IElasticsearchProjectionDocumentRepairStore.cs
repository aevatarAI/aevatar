using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

public interface IElasticsearchProjectionDocumentRepairStore<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    Task<ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey>?> InspectAsync(
        TKey key,
        CancellationToken ct = default);

    Task<ElasticsearchProjectionDocumentRepairDeleteDisposition> DeleteIfUnchangedAsync(
        ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey> lease,
        CancellationToken ct = default);
}

public sealed class ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    internal ElasticsearchProjectionDocumentRepairLease(
        TKey key,
        TReadModel document,
        string concreteIndexName,
        long sequenceNumber,
        long primaryTerm)
    {
        Key = key;
        Document = document;
        ConcreteIndexName = concreteIndexName;
        SequenceNumber = sequenceNumber;
        PrimaryTerm = primaryTerm;
    }

    public TKey Key { get; }

    public TReadModel Document { get; }

    internal string ConcreteIndexName { get; }

    internal long SequenceNumber { get; }

    internal long PrimaryTerm { get; }
}

public enum ElasticsearchProjectionDocumentRepairDeleteDisposition
{
    Deleted = 0,
    AlreadyAbsent = 1,
    RevisionConflict = 2,
}
