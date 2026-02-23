using System.Text.Json;

namespace Aevatar.CQRS.Projection.Providers.InMemory.Stores;

public sealed class InMemoryProjectionReadModelStore<TReadModel, TKey>
    : IProjectionReadModelStore<TReadModel, TKey>,
      IProjectionReadModelStoreProviderMetadata
    where TReadModel : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TReadModel> _itemsByKey = new(StringComparer.Ordinal);
    private readonly Func<TReadModel, TKey> _keySelector;
    private readonly Func<TKey, string> _keyFormatter;
    private readonly Func<TReadModel, object?>? _listSortSelector;
    private readonly int _listTakeMax;
    private readonly JsonSerializerOptions _jsonOptions = new();

    public InMemoryProjectionReadModelStore(
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, object?>? listSortSelector = null,
        int listTakeMax = 200,
        string providerName = ProjectionReadModelProviderNames.InMemory)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _keySelector = keySelector;
        _keyFormatter = keyFormatter ?? (key => key?.ToString() ?? "");
        _listSortSelector = listSortSelector;
        _listTakeMax = listTakeMax > 0 ? listTakeMax : 200;
        ProviderCapabilities = new ProjectionReadModelProviderCapabilities(
            providerName,
            supportsIndexing: false);
    }

    public ProjectionReadModelProviderCapabilities ProviderCapabilities { get; }

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        var key = ResolveReadModelKey(readModel);
        lock (_gate)
            _itemsByKey[key] = Clone(readModel);

        return Task.CompletedTask;
    }

    public Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var keyValue = FormatKey(key);
            if (!_itemsByKey.TryGetValue(keyValue, out var existing))
                throw new InvalidOperationException(
                    $"ReadModel '{typeof(TReadModel).FullName}' with key '{keyValue}' was not found.");

            mutate(existing);
        }

        return Task.CompletedTask;
    }

    public Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var keyValue = FormatKey(key);
            if (!_itemsByKey.TryGetValue(keyValue, out var existing))
                return Task.FromResult<TReadModel?>(null);

            return Task.FromResult<TReadModel?>(Clone(existing));
        }
    }

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var boundedTake = Math.Clamp(take, 1, _listTakeMax);

        lock (_gate)
        {
            IEnumerable<TReadModel> query = _itemsByKey.Values;
            if (_listSortSelector != null)
                query = query.OrderByDescending(_listSortSelector);

            var items = query
                .Take(boundedTake)
                .Select(Clone)
                .ToList();

            return Task.FromResult<IReadOnlyList<TReadModel>>(items);
        }
    }

    private string ResolveReadModelKey(TReadModel readModel)
    {
        var key = _keySelector(readModel);
        var keyValue = FormatKey(key);
        if (keyValue.Length == 0)
        {
            throw new InvalidOperationException(
                $"ReadModel '{typeof(TReadModel).FullName}' resolved an empty key for InMemory store.");
        }

        return keyValue;
    }

    private string FormatKey(TKey key) => _keyFormatter(key)?.Trim() ?? "";

    private TReadModel Clone(TReadModel source)
    {
        var payload = JsonSerializer.Serialize(source, _jsonOptions);
        var clone = JsonSerializer.Deserialize<TReadModel>(payload, _jsonOptions);
        if (clone == null)
            throw new InvalidOperationException(
                $"Failed to clone read model '{typeof(TReadModel).FullName}' in InMemory provider.");

        return clone;
    }
}
