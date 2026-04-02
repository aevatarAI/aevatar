using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class NyxIdServiceApiHintsTests
{
    [Theory]
    [InlineData("bot-telegram-mybot", "Telegram Bot")]
    [InlineData("api-github", "GitHub API")]
    [InlineData("llm-openai", "OpenAI API")]
    [InlineData("chrono-sandbox-service", "Chrono Sandbox")]
    [InlineData("api-slack", "Slack API")]
    [InlineData("api-discord", "Discord API")]
    public void GetHint_KnownSlug_ReturnsHint(string slug, string expectedContains)
    {
        var hint = NyxIdServiceApiHints.GetHint(slug);
        hint.Should().NotBeNull();
        hint.Should().Contain(expectedContains);
    }

    [Theory]
    [InlineData("unknown-service")]
    [InlineData("my-custom-api")]
    [InlineData("")]
    public void GetHint_UnknownSlug_ReturnsNull(string slug)
    {
        var hint = NyxIdServiceApiHints.GetHint(slug);
        hint.Should().BeNull();
    }

    [Fact]
    public void BuildHintsSection_MatchingSlugs_ReturnsFormattedSection()
    {
        var slugs = new[] { "bot-telegram-mybot", "api-github" };
        var section = NyxIdServiceApiHints.BuildHintsSection(slugs);

        section.Should().StartWith("<api-hints>");
        section.Should().EndWith("</api-hints>");
        section.Should().Contain("Telegram Bot");
        section.Should().Contain("GitHub API");
    }

    [Fact]
    public void BuildHintsSection_NoMatches_ReturnsEmpty()
    {
        var slugs = new[] { "unknown-1", "unknown-2" };
        var section = NyxIdServiceApiHints.BuildHintsSection(slugs);

        section.Should().BeEmpty();
    }

    [Fact]
    public void BuildHintsSection_DeduplicatesSamePattern()
    {
        // Two telegram bots should only produce one telegram hints section
        var slugs = new[] { "bot-telegram-one", "bot-telegram-two" };
        var section = NyxIdServiceApiHints.BuildHintsSection(slugs);

        var count = section.Split("### Telegram Bot").Length - 1;
        count.Should().Be(1, "duplicate hints for same pattern should be deduplicated");
    }

    [Fact]
    public void BuildHintsSection_EmptySlugs_ReturnsEmpty()
    {
        var section = NyxIdServiceApiHints.BuildHintsSection([]);
        section.Should().BeEmpty();
    }
}
