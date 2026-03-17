using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aevatar.Tools.Cli.Studio.Domain.Utilities;

public static class JsonNodeExtensions
{
    public static JsonNode? DeepCloneNode(this JsonNode? node) => node?.DeepClone();

    public static bool IsComplexValue(this JsonNode? node) => node is JsonArray or JsonObject;

    public static string? ToWorkflowScalarString(this JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject or JsonArray)
        {
            return node.ToJsonString();
        }

        if (node is not JsonValue value)
        {
            return node.ToJsonString();
        }

        if (value.TryGetValue<string>(out var stringValue))
        {
            return stringValue;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue ? "true" : "false";
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => document.RootElement.GetRawText(),
            JsonValueKind.String => document.RootElement.GetString(),
            _ => document.RootElement.GetRawText(),
        };
    }

    public static object? ToPlainValue(this JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node switch
        {
            JsonObject obj => obj.ToDictionary(
                property => property.Key,
                property => property.Value.ToPlainValue(),
                StringComparer.Ordinal),
            JsonArray array => array.Select(item => item.ToPlainValue()).ToList(),
            _ => node.ToWorkflowScalarString(),
        };
    }
}
