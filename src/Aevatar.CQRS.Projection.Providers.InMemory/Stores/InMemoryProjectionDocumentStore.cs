using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.InMemory.Stores;

public sealed class InMemoryProjectionDocumentStore<TReadModel, TKey>
    : IProjectionDocumentStore<TReadModel, TKey>
    where TReadModel : class
{
    private const string ProviderName = "InMemory";
    private readonly object _gate = new();
    private readonly Dictionary<string, TReadModel> _itemsByKey = new(StringComparer.Ordinal);
    private readonly Func<TReadModel, TKey> _keySelector;
    private readonly Func<TKey, string> _keyFormatter;
    private readonly Func<TReadModel, object?>? _listSortSelector;
    private readonly int _listTakeMax;
    private readonly ILogger<InMemoryProjectionDocumentStore<TReadModel, TKey>> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new();

    public InMemoryProjectionDocumentStore(
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, object?>? listSortSelector = null,
        int listTakeMax = 200,
        ILogger<InMemoryProjectionDocumentStore<TReadModel, TKey>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _keySelector = keySelector;
        _keyFormatter = keyFormatter ?? (key => key?.ToString() ?? "");
        _listSortSelector = listSortSelector;
        _listTakeMax = listTakeMax > 0 ? listTakeMax : 200;
        _logger = logger ?? NullLogger<InMemoryProjectionDocumentStore<TReadModel, TKey>>.Instance;
    }

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        var key = "";
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            key = ResolveReadModelKey(readModel);
            lock (_gate)
                _itemsByKey[key] = Clone(readModel);

            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Projection read-model write completed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
                ProviderName,
                typeof(TReadModel).FullName,
                key,
                elapsedMs,
                "ok");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogError(
                ex,
                "Projection read-model write failed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
                ProviderName,
                typeof(TReadModel).FullName,
                key,
                elapsedMs,
                "failed",
                ex.GetType().Name);
            throw;
        }
    }

    public Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ct.ThrowIfCancellationRequested();

        var keyValue = FormatKey(key);
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            lock (_gate)
            {
                if (!_itemsByKey.TryGetValue(keyValue, out var existing))
                    throw new InvalidOperationException(
                        $"ReadModel '{typeof(TReadModel).FullName}' with key '{keyValue}' was not found.");

                mutate(existing);
            }

            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Projection read-model write completed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
                ProviderName,
                typeof(TReadModel).FullName,
                keyValue,
                elapsedMs,
                "ok");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogError(
                ex,
                "Projection read-model write failed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
                ProviderName,
                typeof(TReadModel).FullName,
                keyValue,
                elapsedMs,
                "failed",
                ex.GetType().Name);
            throw;
        }
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
