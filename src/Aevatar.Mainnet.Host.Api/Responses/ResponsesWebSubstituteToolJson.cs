using System.Text.Json;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal static class ResponsesWebSubstituteToolJson
{
    public static ResponsesWebFetchToolInput ParseFetchInput(string? argumentsJson)
    {
        using var document = ParseObject(argumentsJson);
        var root = document.RootElement;
        return new ResponsesWebFetchToolInput
        {
            Url = ReadString(root, "url") ?? string.Empty,
            ExtractHint = ReadString(root, "extract_hint") ?? string.Empty,
        };
    }

    public static ResponsesWebSearchToolInput ParseSearchInput(string? argumentsJson)
    {
        using var document = ParseObject(argumentsJson);
        var root = document.RootElement;
        return new ResponsesWebSearchToolInput
        {
            Query = ReadString(root, "query") ?? string.Empty,
            MaxResults = ReadInt32(root, "max_results") ?? 0,
        };
    }

    public static string ToBoundaryJson(ResponsesWebSubstituteToolExecutionResult result) =>
        result.ResultCase switch
        {
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.Cached => ToBoundaryJson(result.Cached),
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.Error => ToBoundaryJson(result.Error),
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.Fetch => ToBoundaryJson(result.Fetch),
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.Search => ToBoundaryJson(result.Search),
            _ => "{}",
        };

    private static string ToBoundaryJson(ResponsesWebFetchToolOutput output)
    {
        var value = new Value { StructValue = new Struct() };
        value.StructValue.Fields["url"] = Value.ForString(output.Url);
        value.StructValue.Fields["status_code"] = Value.ForNumber(output.StatusCode);
        value.StructValue.Fields["content_type"] = Value.ForString(output.ContentType);
        value.StructValue.Fields["content"] = Value.ForString(output.Content);
        if (!string.IsNullOrWhiteSpace(output.RedirectUrl))
            value.StructValue.Fields["redirect_url"] = Value.ForString(output.RedirectUrl);
        return ToBoundaryJson(value);
    }

    private static string ToBoundaryJson(Value? value)
    {
        if (value == null || value.KindCase == Value.KindOneofCase.None)
            return "{}";

        using var document = JsonDocument.Parse(JsonFormatter.Default.Format(value));
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static JsonDocument ParseObject(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return JsonDocument.Parse("{}");

        try
        {
            var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;

            document.Dispose();
        }
        catch (JsonException)
        {
        }

        return JsonDocument.Parse("{}");
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? ReadInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }
}
