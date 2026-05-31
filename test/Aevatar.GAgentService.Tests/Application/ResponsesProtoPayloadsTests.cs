using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesProtoPayloadsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json {")]
    public void ParseStruct_ShouldReturnEmptyStruct_ForBlankOrMalformedLegacyJson(string? payload)
    {
        var parsed = ResponsesProtoPayloads.ParseStruct(payload);

        parsed.Fields.Should().BeEmpty();
    }

    [Fact]
    public void ParseStruct_ShouldParseBoundaryJsonObject()
    {
        var parsed = ResponsesProtoPayloads.ParseStruct("""{"city":"Singapore","count":2}""");

        parsed.Fields["city"].StringValue.Should().Be("Singapore");
        parsed.Fields["count"].NumberValue.Should().Be(2);
    }
}
