using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdEffectResultIdentityExtractorTests
{
    [Theory]
    [InlineData("not-json", "/data/id")]
    [InlineData("{}", "/data/id")]
    [InlineData("{\"data\":{\"id\":7}}", "/data/id")]
    [InlineData("{\"data\":{\"id\":\"   \"}}", "/data/id")]
    [InlineData("{\"data\":{\"id\":\"resource-alpha\"}}", "/data/~2id")]
    public void ExtractAtPointer_WhenIdentityIsNotAValidNonEmptyString_ShouldReturnNull(
        string effectResultJson,
        string pointer)
    {
        NyxIdEffectResultIdentityExtractor.ExtractAtPointer(effectResultJson, pointer)
            .Should().BeNull();
    }

    [Fact]
    public void ExtractAtPointer_ShouldResolveEscapedRfc6901SegmentsAndTrimIdentity()
    {
        const string result = "{\"data\":{\"message/id~source\":\"  resource-alpha  \"}}";

        NyxIdEffectResultIdentityExtractor.ExtractAtPointer(
                result,
                "/data/message~1id~0source")
            .Should().Be("resource-alpha");
    }
}
