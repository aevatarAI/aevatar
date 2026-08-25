using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesModelRouteParserTests
{
    [Theory]
    [InlineData("openai/gpt-5", "openai", "gpt-5")]
    [InlineData("o/gpt-5", "o", "gpt-5")]
    [InlineData("vendor-1/model/name", "vendor-1", "model/name")]
    [InlineData("  anthropic/claude-sonnet  ", "anthropic", "claude-sonnet")]
    public void Parse_ShouldSplitSlugPrefix_WhenPrefixIsLowercaseSlug(string model, string routeSlug, string effectiveModel)
    {
        var parsed = ResponsesModelRouteParser.Parse(model);

        parsed.RouteSlug.Should().Be(routeSlug);
        parsed.Model.Should().Be(effectiveModel);
    }

    [Theory]
    [InlineData("OpenAI/gpt-5")]
    [InlineData("/gpt-5")]
    [InlineData("openai/")]
    [InlineData("plain-model")]
    public void Parse_ShouldKeepOriginalModel_WhenPrefixIsNotRouteSlug(string model)
    {
        var parsed = ResponsesModelRouteParser.Parse(model);

        parsed.RouteSlug.Should().BeNull();
        parsed.Model.Should().Be(model.Trim());
    }
}
