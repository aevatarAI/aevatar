using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelTextCommandParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tokenize_ShouldReturnEmpty_ForNullOrWhitespace(string? text)
    {
        var tokens = ChannelTextCommandParser.Tokenize(text);

        tokens.Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_ShouldPreserveQuotedTokens_AndConsumeUnclosedQuotes()
    {
        var tokens = ChannelTextCommandParser.Tokenize(
            "/approve actor_id='actor 1' comment=\"looks good\" edited_content='draft v2");

        tokens.Should().Equal(
            "/approve",
            "actor_id=actor 1",
            "comment=looks good",
            "edited_content=draft v2");
    }

    [Fact]
    public void ParseNamedArguments_ShouldRespectStartIndex_IgnoreMalformedTokens_AndOverwriteCaseInsensitiveKeys()
    {
        var values = ChannelTextCommandParser.ParseNamedArguments(
            [
                "/approve",
                "ignored=first",
                "actor_id=actor-1",
                "bad-token",
                "=missing-key",
                "empty=",
                "USER_INPUT=first",
                "user_input=second",
            ],
            startIndex: 2);

        values.Should().HaveCount(2);
        values["actor_id"].Should().Be("actor-1");
        values["user_input"].Should().Be("second");
    }
}
