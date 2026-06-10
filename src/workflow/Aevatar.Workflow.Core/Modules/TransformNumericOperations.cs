using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

internal static class TransformNumericOperations
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
    };

    public static bool TryExecute(
        string op,
        string input,
        IReadOnlyDictionary<string, string> parameters,
        out string output)
    {
        output = string.Empty;
        if (!TryParseOperation(op, out var operation))
            return false;

        output = operation == NumericOperation.GroupBy
            ? ExecuteGroupBy(input, parameters)
            : FormatDecimal(ExecuteScalar(operation, input, parameters));
        return true;
    }

    private static decimal ExecuteScalar(
        NumericOperation operation,
        string input,
        IReadOnlyDictionary<string, string> parameters)
    {
        var values = ResolveDecimalValues(input, parameters);
        return operation switch
        {
            NumericOperation.Sum => values.Sum(),
            NumericOperation.Subtract => Subtract(values),
            NumericOperation.Multiply => Multiply(values),
            NumericOperation.Divide => Divide(values),
            NumericOperation.Round => Round(values, parameters),
            NumericOperation.Min => values.Min(),
            NumericOperation.Max => values.Max(),
            NumericOperation.GroupBy => throw new InvalidOperationException("group_by is not a scalar numeric operation."),
            _ => throw new InvalidOperationException($"Unsupported numeric operation '{operation}'."),
        };
    }

    private static string ExecuteGroupBy(string input, IReadOnlyDictionary<string, string> parameters)
    {
        var keyPath = WorkflowParameterValueParser.GetString(parameters, string.Empty, "group_by", "key", "key_path").Trim();
        if (string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException("group_by requires a group_by parameter.");

        var valuePath = WorkflowParameterValueParser.GetString(parameters, string.Empty, "field", "value", "value_path").Trim();
        var aggregate = WorkflowParameterValueParser.GetString(parameters, "sum", "aggregate", "agg")
            .Trim()
            .ToLowerInvariant();
        if (!TryParseGroupAggregate(aggregate, out var aggregateOperation))
            throw new InvalidOperationException($"group_by aggregate '{aggregate}' is not supported.");

        using var document = JsonDocument.Parse(input);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("group_by requires a JSON array input.");

        var groups = new SortedDictionary<string, List<decimal>>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var key = ResolveGroupKey(item, keyPath);
            var value = aggregateOperation == GroupAggregate.Count
                ? 1m
                : ParseGroupValue(item, valuePath);

            if (!groups.TryGetValue(key, out var values))
            {
                values = [];
                groups[key] = values;
            }

            values.Add(value);
        }

        var result = new JsonObject();
        foreach (var (key, values) in groups)
        {
            result[key] = FormatDecimal(ApplyAggregate(aggregateOperation, values));
        }

        return JsonSerializer.Serialize(result, JsonOutputOptions);
    }

    private static decimal ParseGroupValue(JsonElement item, string valuePath)
    {
        if (string.IsNullOrWhiteSpace(valuePath))
            throw new InvalidOperationException("group_by requires a field parameter unless aggregate=count.");

        return ParseDecimal(ResolveJsonPath(item, valuePath));
    }

    private static IReadOnlyList<decimal> ResolveDecimalValues(
        string input,
        IReadOnlyDictionary<string, string> parameters)
    {
        var rawValues = WorkflowParameterValueParser.GetString(parameters, input, "values", "value", "numbers");
        var values = ParseDecimalList(rawValues);
        if (values.Count == 0)
            throw new InvalidOperationException("Numeric transform requires at least one decimal value.");

        return values;
    }

    private static List<decimal> ParseDecimalList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var trimmed = raw.Trim();
        if (TryParseJsonDecimalArray(trimmed, out var jsonValues))
            return jsonValues;

        return trimmed
            .Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseDecimalToken)
            .ToList();
    }

    private static bool TryParseJsonDecimalArray(string raw, out List<decimal> values)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                values = [];
                return false;
            }

            values = document.RootElement.EnumerateArray().Select(ParseDecimal).ToList();
            return true;
        }
        catch (JsonException)
        {
            values = [];
            return false;
        }
    }

    private static decimal ParseDecimalToken(string raw)
    {
        var token = raw.Trim();
        if (token.Length >= 2 &&
            ((token[0] == '"' && token[^1] == '"') || (token[0] == '\'' && token[^1] == '\'')))
        {
            token = token[1..^1].Trim();
        }

        if (decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return value;

        throw new InvalidOperationException($"'{raw}' is not a valid decimal value.");
    }

    private static decimal ParseDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number))
            return number;

        if (element.ValueKind == JsonValueKind.String)
            return ParseDecimalToken(element.GetString() ?? string.Empty);

        throw new InvalidOperationException($"JSON value '{element.GetRawText()}' is not a valid decimal value.");
    }

    private static decimal Subtract(IReadOnlyList<decimal> values)
    {
        var result = values[0];
        for (var i = 1; i < values.Count; i++)
            result -= values[i];

        return result;
    }

    private static decimal Multiply(IReadOnlyList<decimal> values)
    {
        var result = 1m;
        foreach (var value in values)
            result *= value;

        return result;
    }

    private static decimal Divide(IReadOnlyList<decimal> values)
    {
        var result = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] == 0m)
                throw new DivideByZeroException("divide cannot use zero as a divisor.");

            result /= values[i];
        }

        return result;
    }

    private static decimal Round(IReadOnlyList<decimal> values, IReadOnlyDictionary<string, string> parameters)
    {
        var digits = WorkflowParameterValueParser.GetBoundedInt(parameters, 0, 0, 28, "digits", "scale", "places");
        return Math.Round(values[0], digits, MidpointRounding.AwayFromZero);
    }

    private static decimal ApplyAggregate(GroupAggregate aggregate, IReadOnlyList<decimal> values) =>
        aggregate switch
        {
            GroupAggregate.Sum => values.Sum(),
            GroupAggregate.Min => values.Min(),
            GroupAggregate.Max => values.Max(),
            GroupAggregate.Count => values.Count,
            _ => throw new InvalidOperationException($"Unsupported group aggregate '{aggregate}'."),
        };

    private static string ResolveGroupKey(JsonElement item, string path)
    {
        var element = ResolveJsonPath(item, path);
        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();
    }

    private static JsonElement ResolveJsonPath(JsonElement element, string path)
    {
        var resolved = element;
        foreach (var segment in SplitPath(path))
        {
            if (resolved.ValueKind == JsonValueKind.Object &&
                resolved.TryGetProperty(segment, out var child))
            {
                resolved = child;
                continue;
            }

            if (resolved.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                TryGetArrayElement(resolved, index, out child))
            {
                resolved = child;
                continue;
            }

            throw new InvalidOperationException($"JSON path '{path}' was not found.");
        }

        return resolved;
    }

    private static bool TryGetArrayElement(JsonElement array, int index, out JsonElement element)
    {
        if (index < 0)
        {
            element = default;
            return false;
        }

        var current = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (current == index)
            {
                element = item;
                return true;
            }

            current++;
        }

        element = default;
        return false;
    }

    private static List<string> SplitPath(string path) =>
        path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static bool TryParseOperation(string op, out NumericOperation operation) =>
        op switch
        {
            "sum" => SetOperation(NumericOperation.Sum, out operation),
            "subtract" => SetOperation(NumericOperation.Subtract, out operation),
            "multiply" => SetOperation(NumericOperation.Multiply, out operation),
            "divide" => SetOperation(NumericOperation.Divide, out operation),
            "round" => SetOperation(NumericOperation.Round, out operation),
            "min" => SetOperation(NumericOperation.Min, out operation),
            "max" => SetOperation(NumericOperation.Max, out operation),
            "group_by" => SetOperation(NumericOperation.GroupBy, out operation),
            _ => SetOperation(default, out operation, false),
        };

    private static bool TryParseGroupAggregate(string aggregate, out GroupAggregate operation) =>
        aggregate switch
        {
            "sum" => SetAggregate(GroupAggregate.Sum, out operation),
            "min" => SetAggregate(GroupAggregate.Min, out operation),
            "max" => SetAggregate(GroupAggregate.Max, out operation),
            "count" => SetAggregate(GroupAggregate.Count, out operation),
            _ => SetAggregate(default, out operation, false),
        };

    private static bool SetOperation(NumericOperation value, out NumericOperation operation, bool matched = true)
    {
        operation = value;
        return matched;
    }

    private static bool SetAggregate(GroupAggregate value, out GroupAggregate operation, bool matched = true)
    {
        operation = value;
        return matched;
    }

    private enum NumericOperation
    {
        Sum,
        Subtract,
        Multiply,
        Divide,
        Round,
        Min,
        Max,
        GroupBy,
    }

    private enum GroupAggregate
    {
        Sum,
        Min,
        Max,
        Count,
    }
}
