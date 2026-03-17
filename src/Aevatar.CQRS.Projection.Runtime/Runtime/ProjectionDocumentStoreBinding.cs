namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreBinding<TReadModel>
    : IProjectionWriteSink<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionDocumentWriter<TReadModel>? _writer;

    public ProjectionDocumentStoreBinding(IProjectionDocumentWriter<TReadModel>? writer = null)
    {
        _writer = writer;
    }

    public bool IsEnabled => _writer is not null;

    public string DisabledReason => IsEnabled
        ? "Document binding is active."
        : "Document projection store service is not registered.";

    public string SinkName => IsEnabled ? "Document" : "Document(Unconfigured)";

    public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        if (_writer is null)
            return Task.FromResult(ProjectionWriteResult.Applied());

        return _writer.UpsertAsync(readModel, ct);
    }
}
