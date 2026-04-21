using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Tests.Projection;

internal sealed class InMemoryProjectionDocumentStore<TReadModel>
    : IProjectionDocumentReader<TReadModel, string>,
      IProjectionWriteDispatcher<TReadModel>
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    private readonly Dictionary<string, TReadModel> _items = new(StringComparer.Ordinal);

    public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);

        ct.ThrowIfCancellationRequested();
        _items[readModel.Id] = Clone(readModel);
        return Task.FromResult(ProjectionWriteResult.Applied());
    }

    public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        ct.ThrowIfCancellationRequested();
        var removed = _items.Remove(id);
        return Task.FromResult(removed
            ? ProjectionWriteResult.Applied()
            : ProjectionWriteResult.Duplicate());
    }

    public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            _items.TryGetValue(key, out var readModel)
                ? Clone(readModel)
                : null);
    }

    public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
        ProjectionDocumentQuery query,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
        {
            Items = _items.Values
                .Take(query.Take <= 0 ? 50 : query.Take)
                .Select(Clone)
                .ToArray(),
        });
    }

    private static TReadModel Clone(TReadModel readModel) => readModel.Clone();
}
