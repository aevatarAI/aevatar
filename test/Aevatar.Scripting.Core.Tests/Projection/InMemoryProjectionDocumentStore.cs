using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Scripting.Core.Tests.Projection;

internal sealed class InMemoryProjectionDocumentStore<TReadModel>
    : IProjectionDocumentStore<TReadModel, string>,
      IProjectionWriteDispatcher<TReadModel, string>
    where TReadModel : class, IProjectionReadModel
{
    private readonly Dictionary<string, TReadModel> _items = new(StringComparer.Ordinal);

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);

        ct.ThrowIfCancellationRequested();
        _items[readModel.Id] = Clone(readModel);
        return Task.CompletedTask;
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

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TReadModel>>(
            _items.Values
                .Take(take)
                .Select(Clone)
                .ToArray());
    }

    private static TReadModel Clone(TReadModel readModel)
    {
        if (readModel is IProjectionReadModelCloneable<TReadModel> cloneable)
            return cloneable.DeepClone();

        return readModel;
    }
}
