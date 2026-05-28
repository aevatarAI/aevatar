using System.Text.Json;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using ProtoValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.Hosting.Tests.Responses;

public sealed class ResponsesWebSubstituteToolJsonTests
{
    [Fact]
    public void ParseFetchInput_NonObjectArg_ReturnsEmptyTypedInput()
    {
        var input = ResponsesWebSubstituteToolJson.ParseFetchInput("""["https://example.com"]""");

        input.Url.Should().BeEmpty();
        input.ExtractHint.Should().BeEmpty();
    }

    [Fact]
    public void ParseFetchInput_MalformedJson_ReturnsEmptyTypedInput()
    {
        var input = ResponsesWebSubstituteToolJson.ParseFetchInput("""{"url":""");

        input.Url.Should().BeEmpty();
        input.ExtractHint.Should().BeEmpty();
    }

    [Fact]
    public void ParseSearchInput_MaxResultsInvalidNumeric_Skipped()
    {
        var input = ResponsesWebSubstituteToolJson.ParseSearchInput(
            """{"query":"aevatar docs","max_results":"5"}""");

        input.Query.Should().Be("aevatar docs");
        input.MaxResults.Should().Be(0);
    }

    [Fact]
    public void ParseSearchInput_MaxResultsValidNumeric_Parsed()
    {
        var input = ResponsesWebSubstituteToolJson.ParseSearchInput(
            """{"query":"aevatar docs","max_results":5}""");

        input.Query.Should().Be("aevatar docs");
        input.MaxResults.Should().Be(5);
    }

    [Fact]
    public void ToBoundaryJson_CachedWebFetchResult_EmitsCachedJson()
    {
        var result = new ResponsesWebSubstituteToolExecutionResult
        {
            Cached = StructValue(
                ("url", ProtoValue.ForString("https://example.com/docs")),
                ("content", ProtoValue.ForString("cached body")),
                ("status_code", ProtoValue.ForNumber(200))),
        };

        using var document = ParseBoundaryJson(result);
        var root = document.RootElement;

        root.GetProperty("url").GetString().Should().Be("https://example.com/docs");
        root.GetProperty("content").GetString().Should().Be("cached body");
        root.GetProperty("status_code").GetInt32().Should().Be(200);
    }

    [Fact]
    public void ToBoundaryJson_FreshWebFetchResult_EmitsFreshJsonWithOptionalRedirect()
    {
        var result = new ResponsesWebSubstituteToolExecutionResult
        {
            Fetch = new ResponsesWebFetchToolOutput
            {
                Url = "https://example.com/docs",
                StatusCode = 200,
                ContentType = "text/html",
                Content = "fresh body",
                RedirectUrl = "https://example.com/final",
            },
        };

        using var document = ParseBoundaryJson(result);
        var root = document.RootElement;

        root.GetProperty("url").GetString().Should().Be("https://example.com/docs");
        root.GetProperty("status_code").GetInt32().Should().Be(200);
        root.GetProperty("content_type").GetString().Should().Be("text/html");
        root.GetProperty("content").GetString().Should().Be("fresh body");
        root.GetProperty("redirect_url").GetString().Should().Be("https://example.com/final");
    }

    [Fact]
    public void ToBoundaryJson_ErrorResult_EmitsErrorJson()
    {
        var result = new ResponsesWebSubstituteToolExecutionResult
        {
            Error = StructValue(("error", ProtoValue.ForString("blocked_private_address"))),
        };

        using var document = ParseBoundaryJson(result);
        var root = document.RootElement;

        root.GetProperty("error").GetString().Should().Be("blocked_private_address");
    }

    [Fact]
    public void ToBoundaryJson_FreshWebSearchEmptyResults_EmitsEmptyResultsJson()
    {
        var result = new ResponsesWebSubstituteToolExecutionResult
        {
            Search = StructValue(("results", ListValue())),
        };

        using var document = ParseBoundaryJson(result);
        var root = document.RootElement;

        root.TryGetProperty("results", out var results).Should().BeTrue();
        results.ValueKind.Should().Be(JsonValueKind.Array);
        results.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void ToBoundaryJson_EmptyProtobufValues_EmitsValidNullableJson()
    {
        var emptyOneof = ResponsesWebSubstituteToolJson.ToBoundaryJson(
            new ResponsesWebSubstituteToolExecutionResult());
        var emptyValue = ResponsesWebSubstituteToolJson.ToBoundaryJson(
            new ResponsesWebSubstituteToolExecutionResult { Cached = new ProtoValue() });
        var emptyFetch = ResponsesWebSubstituteToolJson.ToBoundaryJson(
            new ResponsesWebSubstituteToolExecutionResult { Fetch = new ResponsesWebFetchToolOutput() });
        var emptySearch = ResponsesWebSubstituteToolJson.ToBoundaryJson(
            new ResponsesWebSubstituteToolExecutionResult { Search = StructValue(("results", ListValue())) });

        JsonDocument.Parse(emptyOneof).RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        JsonDocument.Parse(emptyValue).RootElement.ValueKind.Should().Be(JsonValueKind.Object);

        using var fetchDocument = JsonDocument.Parse(emptyFetch);
        fetchDocument.RootElement.GetProperty("url").GetString().Should().BeEmpty();
        fetchDocument.RootElement.GetProperty("status_code").GetInt32().Should().Be(0);
        fetchDocument.RootElement.GetProperty("content_type").GetString().Should().BeEmpty();
        fetchDocument.RootElement.GetProperty("content").GetString().Should().BeEmpty();
        fetchDocument.RootElement.TryGetProperty("redirect_url", out _).Should().BeFalse();

        using var searchDocument = JsonDocument.Parse(emptySearch);
        searchDocument.RootElement.GetProperty("results").GetArrayLength().Should().Be(0);
    }

    private static JsonDocument ParseBoundaryJson(ResponsesWebSubstituteToolExecutionResult result) =>
        JsonDocument.Parse(ResponsesWebSubstituteToolJson.ToBoundaryJson(result));

    private static ProtoValue StructValue(params (string Key, ProtoValue FieldValue)[] fields)
    {
        var value = new ProtoValue { StructValue = new Struct() };
        foreach (var (key, fieldValue) in fields)
            value.StructValue.Fields[key] = fieldValue;
        return value;
    }

    private static ProtoValue ListValue(params ProtoValue[] values)
    {
        var value = new ProtoValue { ListValue = new ListValue() };
        value.ListValue.Values.AddRange(values);
        return value;
    }
}
