using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.StateMirror.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.StateMirror.Services;

public sealed class StateMirrorReadModelProjector<TState, TReadModel, TKey>
    : IStateMirrorReadModelProjector<TState, TReadModel, TKey>
    where TState : class
    where TReadModel : class, IProjectionReadModel
{
    private readonly IStateMirrorProjection<TState, TReadModel> _projection;
    private readonly IProjectionWriteDispatcher<TReadModel, TKey> _writeDispatcher;
    private readonly IProjectionDocumentReader<TReadModel, TKey> _documentReader;

    public StateMirrorReadModelProjector(
        IStateMirrorProjection<TState, TReadModel> projection,
        IProjectionWriteDispatcher<TReadModel, TKey> writeDispatcher,
        IProjectionDocumentReader<TReadModel, TKey> documentReader)
    {
        _projection = projection;
        _writeDispatcher = writeDispatcher;
        _documentReader = documentReader;
    }

    public TReadModel Project(TState state)
    {
        return _projection.Project(state);
    }

    public async Task<TReadModel> ProjectAndUpsertAsync(TState state, CancellationToken ct = default)
    {
        var readModel = Project(state);
        await _writeDispatcher.UpsertAsync(readModel, ct);
        return readModel;
    }

    public Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        return _documentReader.GetAsync(key, ct);
    }

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        return _documentReader.ListAsync(take, ct);
    }
}
