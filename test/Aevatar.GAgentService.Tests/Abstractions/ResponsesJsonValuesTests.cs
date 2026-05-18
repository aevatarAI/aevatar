using System.Text.Json;
using Aevatar.GAgentService.Abstractions.Responses;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using ProtoValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.GAgentService.Tests.Abstractions;

public sealed class ResponsesJsonValuesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseBoundaryPayload_ShouldReturnEmptyObject_ForBlankInput(string? payload)
    {
        var value = ResponsesJsonValues.ParseBoundaryPayload(payload);

        value.KindCase.Should().Be(ProtoValue.KindOneofCase.StructValue);
        ResponsesJsonValues.ToBoundaryJson(value).Should().Be("{}");
    }

    [Fact]
    public void ParseBoundaryPayload_ShouldParseJsonObject_AndReturnCompactBoundaryJson()
    {
        var value = ResponsesJsonValues.ParseBoundaryPayload("""{ "city": "Singapore", "temperature": 28 }""");

        ResponsesJsonValues.ToBoundaryJson(value).Should().Be("""{"city":"Singapore","temperature":28}""");
    }

    [Fact]
    public void ParseBoundaryPayload_ShouldParseJsonArray_AndReturnCompactBoundaryJson()
    {
        var value = ResponsesJsonValues.ParseBoundaryPayload("""[ "alpha", { "done": true } ]""");

        ResponsesJsonValues.ToBoundaryJson(value).Should().Be("""["alpha",{"done":true}]""");
    }

    [Fact]
    public void ParseBoundaryPayload_ShouldPreserveScalarJsonValues()
    {
        ResponsesJsonValues.ToBoundaryJson(ResponsesJsonValues.ParseBoundaryPayload(""" "plain text" """))
            .Should().Be("\"plain text\"");
        ResponsesJsonValues.ToBoundaryJson(ResponsesJsonValues.ParseBoundaryPayload("42"))
            .Should().Be("42");
        ResponsesJsonValues.ToBoundaryJson(ResponsesJsonValues.ParseBoundaryPayload("false"))
            .Should().Be("false");
    }

    [Fact]
    public void ParseBoundaryPayload_ShouldWrapMalformedJson_AsJsonStringValue()
    {
        var value = ResponsesJsonValues.ParseBoundaryPayload("not json {");

        value.KindCase.Should().Be(ProtoValue.KindOneofCase.StringValue);
        ResponsesJsonValues.ToBoundaryJson(value).Should().Be("\"not json {\"");
    }

    [Fact]
    public void ToBoundaryJson_ShouldReturnEmpty_ForNullOrUnsetValue()
    {
        ResponsesJsonValues.ToBoundaryJson(null).Should().BeEmpty();
        ResponsesJsonValues.ToBoundaryJson(new ProtoValue()).Should().BeEmpty();
    }

    [Fact]
    public void ErrorObject_ShouldBuildTypedCompactBoundaryError()
    {
        var json = ResponsesJsonValues.ToBoundaryJson(
            ResponsesJsonValues.ErrorObject("tool_call_expired", "call_1"));

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("error").GetString().Should().Be("tool_call_expired");
        document.RootElement.GetProperty("call_id").GetString().Should().Be("call_1");
        json.Should().NotContain(" ");
    }
}
