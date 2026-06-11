using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Responses;

// Refactor (iter161-cluster-001 #1251-first):
//   Old pattern: Responses Web fetch/search/error results moved through google.protobuf.Value
//   as the normal internal contract, so actor state, cache, readmodels, and Host JSON each
//   interpreted the same untyped payload shape.
//   New principle: ResponsesWebToolResult is the typed internal contract; this migration
//   utility only bridges legacy Value payloads to and from the typed contract.
public static class ResponsesWebResultMigration
{
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

    // Refactor (iter161-cluster-001 #1251-first):
    //   Old pattern: query snapshots exposed legacy google.protobuf.Value directly.
    //   New principle: old readmodels are lifted into typed ResponsesWebToolResult before
    //   leaving the projection query boundary.
    public static ResponsesWebToolResult FromLegacyValue(Value? value)
    {
        if (value == null || value.KindCase == Value.KindOneofCase.None)
            return new ResponsesWebToolResult();

        if (value.KindCase != Value.KindOneofCase.StructValue)
            return FromError("legacy_value_result", ReadValueAsString(value));

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

        return FromError("legacy_value_result", "unsupported legacy result value");
    }

    // Refactor (iter161-cluster-001 #1251-first):
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
            _ => "unsupported legacy result value",
        };
}
