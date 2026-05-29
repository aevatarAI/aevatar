using System.Text.Json;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
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

    // Refactor (iter161-cluster-001 #1251-first):
    //   Old pattern: shared Abstractions rendered Web result JSON before Host boundary handling.
    //   New principle: Host owns final external JSON rendering from typed Web result branches.
    public static string ToBoundaryJson(ResponsesWebSubstituteToolExecutionResult result) =>
        result.ResultCase switch
        {
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.TypedCached => ResponsesWebResultBoundaryJson.ToBoundaryJson(result.TypedCached),
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.TypedError => ResponsesWebResultBoundaryJson.ToBoundaryJson(result.TypedError),
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.TypedSearch => ResponsesWebResultBoundaryJson.ToBoundaryJson(result.TypedSearch),
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.Cached => ToBoundaryJson(result.Cached),
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.Error => ToBoundaryJson(result.Error),
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.Fetch => ResponsesWebResultBoundaryJson.ToBoundaryJson(result.Fetch),
            ResponsesWebSubstituteToolExecutionResult.ResultOneofCase.Search => ToBoundaryJson(result.Search),
            _ => "{}",
        };

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
