using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using ProtoValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.GAgentService.Tests.Abstractions;

public sealed class ResponsesWebResultMigrationTests
{
    [Fact]
    public void FromLegacyValue_ShouldMapFetchStruct_ToTypedFetchResult()
    {
        var value = new ProtoValue { StructValue = new Struct() };
        value.StructValue.Fields["url"] = ProtoValue.ForString("https://example.com/page");
        value.StructValue.Fields["status_code"] = ProtoValue.ForNumber(201);
        value.StructValue.Fields["content_type"] = ProtoValue.ForString("text/html");
        value.StructValue.Fields["content"] = ProtoValue.ForString("body");
        value.StructValue.Fields["redirect_url"] = ProtoValue.ForString("https://example.com/final");

        var result = ResponsesWebResultMigration.FromLegacyValue(value);

        result.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.Fetch);
        result.Fetch.Url.Should().Be("https://example.com/page");
        result.Fetch.StatusCode.Should().Be(201);
        result.Fetch.ContentType.Should().Be("text/html");
        result.Fetch.Content.Should().Be("body");
        result.Fetch.RedirectUrl.Should().Be("https://example.com/final");
    }

    [Fact]
    public void FromLegacyValue_ShouldMapSearchStruct_ToTypedSearchResult()
    {
        var value = new ProtoValue { StructValue = new Struct() };
        var results = new ListValue();
        var first = new ProtoValue { StructValue = new Struct() };
        first.StructValue.Fields["title"] = ProtoValue.ForString("First");
        first.StructValue.Fields["url"] = ProtoValue.ForString("https://example.com/1");
        first.StructValue.Fields["snippet"] = ProtoValue.ForString("Snippet");
        results.Values.Add(first);
        results.Values.Add(ProtoValue.ForString("ignored"));
        value.StructValue.Fields["results"] = new ProtoValue { ListValue = results };

        var result = ResponsesWebResultMigration.FromLegacyValue(value);

        result.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.Search);
        result.Search.Results.Should().ContainSingle();
        result.Search.Results[0].Title.Should().Be("First");
        result.Search.Results[0].Url.Should().Be("https://example.com/1");
        result.Search.Results[0].Snippet.Should().Be("Snippet");
    }

    [Fact]
    public void FromLegacyValue_ShouldMapErrorStruct_ToTypedErrorResult()
    {
        var value = new ProtoValue { StructValue = new Struct() };
        value.StructValue.Fields["error"] = ProtoValue.ForString("auth_failed");

        var result = ResponsesWebResultMigration.FromLegacyValue(value);

        result.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.Error);
        result.Error.Code.Should().Be("auth_failed");
        result.Error.Message.Should().Be("auth_failed");
    }

    [Fact]
    public void FromLegacyValue_ShouldMapUnknownStruct_ToLegacyValueError()
    {
        var value = new ProtoValue { StructValue = new Struct() };
        value.StructValue.Fields["custom"] = ProtoValue.ForString("kept");

        var result = ResponsesWebResultMigration.FromLegacyValue(value);

        result.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.Error);
        result.Error.Code.Should().Be("legacy_value_result");
        result.Error.Message.Should().Be("unsupported legacy result value");
    }

    [Fact]
    public void FromLegacyValue_ShouldMapNonStructValue_ToLegacyValueError()
    {
        var result = ResponsesWebResultMigration.FromLegacyValue(ProtoValue.ForString("plain"));

        result.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.Error);
        result.Error.Code.Should().Be("legacy_value_result");
        result.Error.Message.Should().Be("plain");
    }

    [Fact]
    public void FromLegacyValue_ShouldReturnUnsetResult_ForEmptyValue()
    {
        ResponsesWebResultMigration.FromLegacyValue(null).ResultCase
            .Should().Be(ResponsesWebToolResult.ResultOneofCase.None);
        ResponsesWebResultMigration.FromLegacyValue(new ProtoValue()).ResultCase
            .Should().Be(ResponsesWebToolResult.ResultOneofCase.None);
    }

    [Fact]
    public void ToLegacyValue_ShouldMapSearchResult_ToLegacySearchStruct()
    {
        var result = ResponsesWebResultMigration.FromSearch(new ResponsesWebSearchToolOutput
        {
            Results =
            {
                new ResponsesWebSearchResultItem
                {
                    Title = "First",
                    Url = "https://example.com/1",
                    Snippet = "Snippet",
                },
            },
        });

        var value = ResponsesWebResultMigration.ToLegacyValue(result);

        value.KindCase.Should().Be(ProtoValue.KindOneofCase.StructValue);
        var results = value.StructValue.Fields["results"].ListValue.Values;
        results.Should().ContainSingle();
        results[0].StructValue.Fields["title"].StringValue.Should().Be("First");
        results[0].StructValue.Fields["url"].StringValue.Should().Be("https://example.com/1");
        results[0].StructValue.Fields["snippet"].StringValue.Should().Be("Snippet");
    }

    [Fact]
    public void ToLegacyValue_ShouldMapErrorResult_ToLegacyErrorStruct()
    {
        var result = ResponsesWebResultMigration.FromError("auth_failed", "Token missing");

        var value = ResponsesWebResultMigration.ToLegacyValue(result);

        value.KindCase.Should().Be(ProtoValue.KindOneofCase.StructValue);
        value.StructValue.Fields["error"].StringValue.Should().Be("auth_failed");
        value.StructValue.Fields["message"].StringValue.Should().Be("Token missing");
    }
}
