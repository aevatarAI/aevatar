using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.InMemory.Stores;

public sealed class InMemoryProjectionDocumentStore<TReadModel, TKey>
    : IProjectionDocumentReader<TReadModel, TKey>,
      IProjectionDocumentWriter<TReadModel>
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    private const string ProviderName = "InMemory";
    private readonly object _gate = new();
    private readonly Dictionary<string, TReadModel> _itemsByKey = new(StringComparer.Ordinal);
    private readonly Func<TReadModel, TKey> _keySelector;
    private readonly Func<TKey, string> _keyFormatter;
    private readonly Func<TReadModel, object?>? _defaultSortSelector;
    private readonly int _queryTakeMax;
    private readonly ILogger<InMemoryProjectionDocumentStore<TReadModel, TKey>> _logger;

    public InMemoryProjectionDocumentStore(
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, object?>? defaultSortSelector = null,
        int queryTakeMax = 200,
        ILogger<InMemoryProjectionDocumentStore<TReadModel, TKey>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _keySelector = keySelector;
        _keyFormatter = keyFormatter ?? (key => key?.ToString() ?? "");
        _defaultSortSelector = defaultSortSelector;
        _queryTakeMax = queryTakeMax > 0 ? queryTakeMax : 200;
        _logger = logger ?? NullLogger<InMemoryProjectionDocumentStore<TReadModel, TKey>>.Instance;
    }

    public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        var key = "";
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            key = ResolveReadModelKey(readModel);
            ProjectionWriteResult result;
            lock (_gate)
            {
                _itemsByKey.TryGetValue(key, out var existing);
                result = ProjectionWriteResultEvaluator.Evaluate(existing, readModel);
                if (result.IsApplied)
                    _itemsByKey[key] = Clone(readModel);
            }

            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Projection read-model write completed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
                ProviderName,
                typeof(TReadModel).FullName,
                key,
                elapsedMs,
                result.Disposition);
            return Task.FromResult(result);
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

    public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();

        var trimmedId = id.Trim();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            bool removed;
            lock (_gate)
                removed = _itemsByKey.Remove(trimmedId);

            var result = removed
                ? ProjectionWriteResult.Applied()
                : ProjectionWriteResult.Duplicate();
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Projection read-model delete completed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}",
                ProviderName,
                typeof(TReadModel).FullName,
                trimmedId,
                elapsedMs,
                result.Disposition);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            _logger.LogError(
                ex,
                "Projection read-model delete failed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}",
                ProviderName,
                typeof(TReadModel).FullName,
                trimmedId,
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

    public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
        ProjectionDocumentQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var boundedTake = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, _queryTakeMax);
        var offset = DecodeCursor(query.Cursor);
        List<TReadModel> snapshot;

        lock (_gate)
            snapshot = _itemsByKey.Values.ToList();

        var filtered = query.Filters.Count == 0
            ? snapshot
            : snapshot.Where(item => MatchesAllFilters(item, query.Filters)).ToList();
        filtered.Sort((left, right) => CompareReadModels(left, right, query.Sorts));

        var totalCount = query.IncludeTotalCount ? filtered.Count : (long?)null;
        if (offset >= filtered.Count)
        {
            return Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
            {
                Items = [],
                NextCursor = null,
                TotalCount = totalCount,
            });
        }

        var items = filtered
            .Skip(offset)
            .Take(boundedTake)
            .Select(Clone)
            .ToList();
        var nextOffset = offset + items.Count;

        return Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
        {
            Items = items,
            NextCursor = nextOffset < filtered.Count ? EncodeCursor(nextOffset) : null,
            TotalCount = totalCount,
        });
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

    private int CompareReadModels(
        TReadModel left,
        TReadModel right,
        IReadOnlyList<ProjectionDocumentSort> sorts)
    {
        if (sorts.Count > 0)
        {
            foreach (var sort in sorts)
            {
                var comparison = CompareSortValues(
                    ResolveFieldValue(left, sort.FieldPath),
                    ResolveFieldValue(right, sort.FieldPath),
                    sort.Direction);
                if (comparison != 0)
                    return comparison;
            }
        }
        else if (_defaultSortSelector != null)
        {
            var comparison = CompareSortValues(
                _defaultSortSelector(left),
                _defaultSortSelector(right),
                ProjectionDocumentSortDirection.Desc);
            if (comparison != 0)
                return comparison;
        }

        return string.CompareOrdinal(ResolveReadModelKey(right), ResolveReadModelKey(left));
    }

    private static int CompareSortValues(
        object? left,
        object? right,
        ProjectionDocumentSortDirection direction)
    {
        var normalizedLeft = NormalizeComparableValue(left);
        var normalizedRight = NormalizeComparableValue(right);
        var comparison = CompareNormalizedValues(normalizedLeft, normalizedRight);
        return direction == ProjectionDocumentSortDirection.Desc
            ? -comparison
            : comparison;
    }

    private static int CompareNormalizedValues(object? left, object? right)
    {
        if (left == null && right == null)
            return 0;
        if (left == null)
            return 1;
        if (right == null)
            return -1;

        if (TryCompareNumbers(left, right, out var numericComparison))
            return numericComparison;

        if (left is DateTime leftDateTime && right is DateTime rightDateTime)
            return leftDateTime.CompareTo(rightDateTime);

        if (left is bool leftBool && right is bool rightBool)
            return leftBool.CompareTo(rightBool);

        return string.CompareOrdinal(left.ToString(), right.ToString());
    }

    private static bool TryCompareNumbers(object left, object right, out int comparison)
    {
        if (!TryConvertToDouble(left, out var leftValue) || !TryConvertToDouble(right, out var rightValue))
        {
            comparison = 0;
            return false;
        }

        comparison = leftValue.CompareTo(rightValue);
        return true;
    }

    private static bool TryConvertToDouble(object value, out double numeric)
    {
        switch (value)
        {
            case byte byteValue:
                numeric = byteValue;
                return true;
            case sbyte sbyteValue:
                numeric = sbyteValue;
                return true;
            case short shortValue:
                numeric = shortValue;
                return true;
            case ushort ushortValue:
                numeric = ushortValue;
                return true;
            case int intValue:
                numeric = intValue;
                return true;
            case uint uintValue:
                numeric = uintValue;
                return true;
            case long longValue:
                numeric = longValue;
                return true;
            case ulong ulongValue:
                numeric = ulongValue;
                return true;
            case float floatValue:
                numeric = floatValue;
                return true;
            case double doubleValue:
                numeric = doubleValue;
                return true;
            case decimal decimalValue:
                numeric = (double)decimalValue;
                return true;
            default:
                numeric = 0;
                return false;
        }
    }

    private static object? NormalizeComparableValue(object? value)
    {
        return value switch
        {
            null => null,
            DateTime dateTime => dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime(),
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            Enum enumValue => enumValue.ToString(),
            Guid guid => guid.ToString(),
            _ => value,
        };
    }

    private static object? ResolveFieldValue(object? source, string fieldPath)
    {
        if (source == null || string.IsNullOrWhiteSpace(fieldPath))
            return null;

        object? current = source;
        foreach (var segment in fieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current == null)
                return null;

            if (current is IDictionary<string, object?> typedDictionary)
            {
                if (!TryGetDictionaryValue(typedDictionary, segment, out current))
                    return null;

                continue;
            }

            if (current is IDictionary dictionary)
            {
                if (!TryGetDictionaryValue(dictionary, segment, out current))
                    return null;

                continue;
            }

            var property = current.GetType().GetProperty(
                segment,
                BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
                return null;

            current = property.GetValue(current);
        }

        return current;
    }

    private static bool TryGetDictionaryValue(
        IDictionary<string, object?> dictionary,
        string key,
        out object? value)
    {
        return dictionary.TryGetValue(key, out value);
    }

    private static bool TryGetDictionaryValue(
        IDictionary dictionary,
        string key,
        out object? value)
    {
        if (dictionary.Contains(key))
        {
            value = dictionary[key];
            return true;
        }

        value = null;
        return false;
    }

    private static bool MatchesAllFilters(
        TReadModel readModel,
        IReadOnlyList<ProjectionDocumentFilter> filters)
    {
        foreach (var filter in filters)
        {
            if (!MatchesFilter(readModel, filter))
                return false;
        }

        return true;
    }

    private static bool MatchesFilter(TReadModel readModel, ProjectionDocumentFilter filter)
    {
        if (string.IsNullOrWhiteSpace(filter.FieldPath))
            return false;

        var actualValue = NormalizeComparableValue(ResolveFieldValue(readModel, filter.FieldPath));
        return filter.Operator switch
        {
            ProjectionDocumentFilterOperator.Exists => actualValue != null,
            ProjectionDocumentFilterOperator.Eq => CompareNormalizedValues(actualValue, GetScalarValue(filter.Value)) == 0,
            ProjectionDocumentFilterOperator.In => GetCollectionValues(filter.Value)
                .Any(expected => CompareNormalizedValues(actualValue, expected) == 0),
            ProjectionDocumentFilterOperator.Gt => CompareNormalizedValues(actualValue, GetScalarValue(filter.Value)) > 0,
            ProjectionDocumentFilterOperator.Gte => CompareNormalizedValues(actualValue, GetScalarValue(filter.Value)) >= 0,
            ProjectionDocumentFilterOperator.Lt => CompareNormalizedValues(actualValue, GetScalarValue(filter.Value)) < 0,
            ProjectionDocumentFilterOperator.Lte => CompareNormalizedValues(actualValue, GetScalarValue(filter.Value)) <= 0,
            _ => false,
        };
    }

    private static object? GetScalarValue(ProjectionDocumentValue value)
    {
        return value.Kind switch
        {
            ProjectionDocumentValueKind.String => value.RawValue as string,
            ProjectionDocumentValueKind.Int64 => value.RawValue,
            ProjectionDocumentValueKind.Double => value.RawValue,
            ProjectionDocumentValueKind.Bool => value.RawValue,
            ProjectionDocumentValueKind.DateTime => NormalizeComparableValue(value.RawValue),
            ProjectionDocumentValueKind.StringList when value.RawValue is string[] stringValues && stringValues.Length > 0 => stringValues[0],
            ProjectionDocumentValueKind.Int64List when value.RawValue is long[] int64Values && int64Values.Length > 0 => int64Values[0],
            ProjectionDocumentValueKind.DoubleList when value.RawValue is double[] doubleValues && doubleValues.Length > 0 => doubleValues[0],
            ProjectionDocumentValueKind.BoolList when value.RawValue is bool[] boolValues && boolValues.Length > 0 => boolValues[0],
            ProjectionDocumentValueKind.DateTimeList when value.RawValue is DateTime[] dateTimes && dateTimes.Length > 0 => NormalizeComparableValue(dateTimes[0]),
            _ => null,
        };
    }

    private static IReadOnlyList<object?> GetCollectionValues(ProjectionDocumentValue value)
    {
        return value.Kind switch
        {
            ProjectionDocumentValueKind.StringList when value.RawValue is string[] stringValues =>
                stringValues.Cast<object?>().ToArray(),
            ProjectionDocumentValueKind.Int64List when value.RawValue is long[] int64Values =>
                int64Values.Cast<object?>().ToArray(),
            ProjectionDocumentValueKind.DoubleList when value.RawValue is double[] doubleValues =>
                doubleValues.Cast<object?>().ToArray(),
            ProjectionDocumentValueKind.BoolList when value.RawValue is bool[] boolValues =>
                boolValues.Cast<object?>().ToArray(),
            ProjectionDocumentValueKind.DateTimeList when value.RawValue is DateTime[] dateTimes =>
                dateTimes.Select(static x => NormalizeComparableValue(x)).ToArray(),
            ProjectionDocumentValueKind.String or
            ProjectionDocumentValueKind.Int64 or
            ProjectionDocumentValueKind.Double or
            ProjectionDocumentValueKind.Bool or
            ProjectionDocumentValueKind.DateTime =>
                [GetScalarValue(value)],
            _ => [],
        };
    }

    private static string EncodeCursor(int offset)
    {
        var payload = Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(payload);
    }

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;

        try
        {
            var payload = Convert.FromBase64String(cursor);
            var rawOffset = Encoding.UTF8.GetString(payload);
            if (int.TryParse(rawOffset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) &&
                offset >= 0)
            {
                return offset;
            }
        }
        catch (FormatException)
        {
        }

        throw new InvalidOperationException("Invalid InMemory projection document query cursor.");
    }

    private static TReadModel Clone(TReadModel source) => source.Clone();
}
