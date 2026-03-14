using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgentService.Tests.Projection;

internal sealed class FixedProjectionClock : IProjectionClock
{
    public FixedProjectionClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; }
}

internal sealed class RecordingDocumentStore<TReadModel> :
    IProjectionDocumentStore<TReadModel, string>,
    IProjectionStoreDispatcher<TReadModel, string>
    where TReadModel : class, IProjectionReadModel
{
    private readonly Func<TReadModel, string> _keySelector;
    private readonly List<TReadModel> _items = [];

    public RecordingDocumentStore(Func<TReadModel, string> keySelector)
    {
        _keySelector = keySelector;
    }

    public int LastListTake { get; private set; }

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        var key = _keySelector(readModel);
        var existingIndex = _items.FindIndex(x => string.Equals(_keySelector(x), key, StringComparison.Ordinal));
        if (existingIndex >= 0)
            _items[existingIndex] = readModel;
        else
            _items.Add(readModel);

        return Task.CompletedTask;
    }

    public Task MutateAsync(string key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        var item = _items.FirstOrDefault(x => string.Equals(_keySelector(x), key, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Read model '{key}' was not found.");
        mutate(item);
        return Task.CompletedTask;
    }

    public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_items.FirstOrDefault(x => string.Equals(_keySelector(x), key, StringComparison.Ordinal)));

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        LastListTake = take;
        return Task.FromResult<IReadOnlyList<TReadModel>>(_items.Take(take).ToList());
    }
}
