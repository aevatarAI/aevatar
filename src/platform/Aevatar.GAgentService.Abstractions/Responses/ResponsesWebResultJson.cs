using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Responses;

// Refactor (issue1251-first):
//   Old pattern: Responses Web fetch/search/error results moved through google.protobuf.Value
//   as the normal internal contract, so actor state, cache, readmodels, and Host JSON each
//   interpreted the same untyped payload shape.
//   New principle: ResponsesWebToolResult is the typed internal contract; this boundary
//   utility intentionally keeps the three transition duties together while the old Value
//   surface remains only for legacy readmodel fallback and external JSON formatting.
public static class ResponsesWebResultJson
{
    private static readonly JsonFormatter Formatter = new(new JsonFormatter.Settings(formatDefaultValues: false));

    public static ResponsesWebToolResult FromFetch(ResponsesWebFetchToolOutput output) =>
        new()
        {
            Fetch = output.Clone(),
        };

    public static ResponsesWebToolResult FromSearch(ResponsesWebSearchToolOutput output) =>
        new()
        {
            Search = output.Clone(),
        };

    public static ResponsesWebToolResult FromError(string code, string message = "") =>
        new()
        {
            Error = new ResponsesWebToolError
            {
                Code = code,
                Message = message,
            },
        };

    // Refactor (issue1251-first):
    //   Old pattern: query snapshots exposed legacy google.protobuf.Value directly.
    //   New principle: old readmodels are lifted into typed ResponsesWebToolResult before
    //   leaving the projection query boundary.
    public static ResponsesWebToolResult FromLegacyValue(Value? value)
    {
        if (value == null || value.KindCase == Value.KindOneofCase.None)
            return new ResponsesWebToolResult();

        if (value.KindCase != Value.KindOneofCase.StructValue)
            return FromError("legacy_value_result", ToBoundaryJson(value));

        var fields = value.StructValue.Fields;
        if (fields.TryGetValue("results", out var results) &&
            results.KindCase == Value.KindOneofCase.ListValue)
        {
            return FromSearch(new ResponsesWebSearchToolOutput
            {
                Results =
                {
                    results.ListValue.Values
                        .Where(static item => item.KindCase == Value.KindOneofCase.StructValue)
                        .Select(static item => new ResponsesWebSearchResultItem
                        {
                            Title = ReadString(item.StructValue.Fields, "title"),
                            Url = ReadString(item.StructValue.Fields, "url"),
                            Snippet = ReadString(item.StructValue.Fields, "snippet"),
                        }),
                },
            });
        }

        if (fields.TryGetValue("error", out var error))
            return FromError(ReadValueAsString(error), ReadValueAsString(error));

        if (fields.ContainsKey("url") ||
            fields.ContainsKey("status_code") ||
            fields.ContainsKey("content") ||
            fields.ContainsKey("content_type"))
        {
            return FromFetch(new ResponsesWebFetchToolOutput
            {
                Url = ReadString(fields, "url"),
                StatusCode = ReadInt32(fields, "status_code"),
                ContentType = ReadString(fields, "content_type"),
                Content = ReadString(fields, "content"),
                RedirectUrl = ReadString(fields, "redirect_url"),
            });
        }

        return FromError("legacy_value_result", ToBoundaryJson(value));
    }

    // Refactor (issue1251-first):
    //   Old pattern: Host-facing JSON was assembled from whichever untyped Value branch
    //   happened to be present.
    //   New principle: boundary JSON is rendered from typed ResponsesWebToolResult, with
    //   legacy Value formatting confined to explicit fallback conversion.
    public static string ToBoundaryJson(ResponsesWebToolResult? result)
    {
        if (result == null)
            return "{}";

        return result.ResultCase switch
        {
            ResponsesWebToolResult.ResultOneofCase.Fetch => ToBoundaryJson(result.Fetch),
            ResponsesWebToolResult.ResultOneofCase.Search => ToBoundaryJson(result.Search),
            ResponsesWebToolResult.ResultOneofCase.Error => ToBoundaryJson(result.Error),
            _ => "{}",
        };
    }

    public static string ToBoundaryJson(ResponsesWebFetchToolOutput? output)
    {
        var fields = new Dictionary<string, object?>
        {
            ["url"] = output?.Url ?? string.Empty,
            ["status_code"] = output?.StatusCode ?? 0,
            ["content_type"] = output?.ContentType ?? string.Empty,
            ["content"] = output?.Content ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(output?.RedirectUrl))
            fields["redirect_url"] = output.RedirectUrl;

        return JsonSerializer.Serialize(fields);
    }

    public static string ToBoundaryJson(ResponsesWebSearchToolOutput? output) =>
        JsonSerializer.Serialize(new
        {
            results = (output?.Results ?? Enumerable.Empty<ResponsesWebSearchResultItem>())
                .Select(static item => new
                {
                    title = item.Title,
                    url = item.Url,
                    snippet = item.Snippet,
                }),
        });

    public static string ToBoundaryJson(ResponsesWebToolError? error) =>
        JsonSerializer.Serialize(new
        {
            error = error?.Code ?? string.Empty,
            message = error?.Message ?? string.Empty,
        });

    // Refactor (issue1251-first):
    //   Old pattern: normal writes persisted stable Web result semantics as Value.
    //   New principle: writes carry typed ResponsesWebToolResult; Value is emitted only
    //   for legacy storage/readmodel compatibility during the migration slice.
    public static Value ToLegacyValue(ResponsesWebToolResult? result)
    {
        if (result == null)
            return new Value();

        return result.ResultCase switch
        {
            ResponsesWebToolResult.ResultOneofCase.Fetch => ToLegacyValue(result.Fetch),
            ResponsesWebToolResult.ResultOneofCase.Search => ToLegacyValue(result.Search),
            ResponsesWebToolResult.ResultOneofCase.Error => ToLegacyValue(result.Error),
            _ => new Value(),
        };
    }

    private static Value ToLegacyValue(ResponsesWebFetchToolOutput? output)
    {
        if (output == null)
            return new Value();

        var value = new Value { StructValue = new Struct() };
        value.StructValue.Fields["url"] = Value.ForString(output.Url ?? string.Empty);
        value.StructValue.Fields["status_code"] = Value.ForNumber(output.StatusCode);
        value.StructValue.Fields["content_type"] = Value.ForString(output.ContentType ?? string.Empty);
        value.StructValue.Fields["content"] = Value.ForString(output.Content ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(output.RedirectUrl))
            value.StructValue.Fields["redirect_url"] = Value.ForString(output.RedirectUrl);
        return value;
    }

    private static Value ToLegacyValue(ResponsesWebSearchToolOutput? output)
    {
        var value = new Value { StructValue = new Struct() };
        var results = new ListValue();
        foreach (var item in output?.Results ?? Enumerable.Empty<ResponsesWebSearchResultItem>())
        {
            var itemValue = new Value { StructValue = new Struct() };
            itemValue.StructValue.Fields["title"] = Value.ForString(item.Title ?? string.Empty);
            itemValue.StructValue.Fields["url"] = Value.ForString(item.Url ?? string.Empty);
            itemValue.StructValue.Fields["snippet"] = Value.ForString(item.Snippet ?? string.Empty);
            results.Values.Add(itemValue);
        }

        value.StructValue.Fields["results"] = new Value { ListValue = results };
        return value;
    }

    private static Value ToLegacyValue(ResponsesWebToolError? error)
    {
        if (error == null)
            return new Value();

        var value = new Value { StructValue = new Struct() };
        value.StructValue.Fields["error"] = Value.ForString(error.Code ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(error.Message))
            value.StructValue.Fields["message"] = Value.ForString(error.Message);
        return value;
    }

    private static string ToBoundaryJson(Value value)
    {
        using var document = JsonDocument.Parse(Formatter.Format(value));
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static string ReadString(IDictionary<string, Value> fields, string name) =>
        fields.TryGetValue(name, out var value) ? ReadValueAsString(value) : string.Empty;

    private static int ReadInt32(IDictionary<string, Value> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
            return 0;

        return value.KindCase == Value.KindOneofCase.NumberValue
            ? (int)value.NumberValue
            : 0;
    }

    private static string ReadValueAsString(Value value) =>
        value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            Value.KindOneofCase.NullValue => string.Empty,
            _ => ToBoundaryJson(value),
        };
}
