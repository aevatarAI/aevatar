namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreBinding<TReadModel, TKey>
    : IProjectionStoreBinding<TReadModel, TKey>,
      IProjectionStoreBindingAvailability
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionDocumentWriter<TReadModel>? _writer;

    public ProjectionDocumentStoreBinding(IProjectionDocumentWriter<TReadModel>? writer = null)
    {
        _writer = writer;
    }

    public bool IsConfigured => _writer is not null;

    public string AvailabilityReason => IsConfigured
        ? "Document binding is active."
        : "Document projection store service is not registered.";

    public string StoreName => IsConfigured ? "Document" : "Document(Unconfigured)";

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        if (_writer is null)
            return Task.CompletedTask;

        return _writer.UpsertAsync(readModel, ct);
    }
}
