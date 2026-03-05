using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.Stores;

/// <summary>
/// Lightweight in-memory store for dev/test. Does not clone on read —
/// production deployments should register an Elasticsearch-backed store instead.
/// </summary>
public sealed class AppInMemoryDocumentStore<TReadModel, TKey>
    : IProjectionDocumentStore<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TReadModel> _data = new(StringComparer.Ordinal);
    private readonly Func<TReadModel, TKey> _keySelector;
    private readonly Func<TKey, string> _keyFormatter;

    public AppInMemoryDocumentStore(
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _keySelector = keySelector;
        _keyFormatter = keyFormatter ?? (k => k?.ToString() ?? "");
    }

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = FormatKey(_keySelector(readModel));
        lock (_gate)
            _data[key] = readModel;
        return Task.CompletedTask;
    }

    public Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var k = FormatKey(key);
        lock (_gate)
        {
            if (!_data.TryGetValue(k, out var model))
            {
                model = Activator.CreateInstance<TReadModel>();
                _data[k] = model;
            }
            mutate(model);
        }
        return Task.CompletedTask;
    }

    public Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(_data.TryGetValue(FormatKey(key), out var m) ? m : null);
    }

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var list = _data.Values.Take(Math.Clamp(take, 1, 200)).ToList();
            return Task.FromResult<IReadOnlyList<TReadModel>>(list);
        }
    }

    private string FormatKey(TKey key) => _keyFormatter(key)?.Trim() ?? "";
}
