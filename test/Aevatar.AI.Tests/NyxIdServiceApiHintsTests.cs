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

    [Fact]
    public void BuildHintFromOperations_GeneratesFormattedHint()
    {
        var operations = new[]
        {
            new OperationCard("test", "list_items", "GET", "/items", "List all items", null, null),
            new OperationCard("test", "create_item", "POST", "/items", "Create an item", null, null),
        };

        var hint = NyxIdServiceApiHints.BuildHintFromOperations("TestService", operations);

        hint.Should().Contain("### TestService API");
        hint.Should().Contain("2 endpoints");
        hint.Should().Contain("GET /items — List all items");
        hint.Should().Contain("POST /items — Create an item");
    }

    [Fact]
    public void BuildHintFromOperations_TruncatesAtMax()
    {
        var operations = Enumerable.Range(0, 20)
            .Select(i => new OperationCard("svc", $"op_{i}", "GET", $"/path/{i}", $"Op {i}", null, null))
            .ToArray();

        var hint = NyxIdServiceApiHints.BuildHintFromOperations("BigService", operations, maxEndpoints: 5);

        hint.Should().Contain("20 endpoints");
        hint.Should().Contain("... and 15 more");
        hint.Should().Contain("nyxid_search_capabilities");
    }

    [Fact]
    public void BuildHintFromOperations_EmptyOperations_ReturnsEmpty()
    {
        var hint = NyxIdServiceApiHints.BuildHintFromOperations("Empty", []);
        hint.Should().BeEmpty();
    }

    [Fact]
    public void BuildHintFromOperations_OmitsSummaryWhenEmpty()
    {
        var operations = new[]
        {
            new OperationCard("test", "op1", "GET", "/path", "", null, null),
        };

        var hint = NyxIdServiceApiHints.BuildHintFromOperations("Svc", operations);

        hint.Should().Contain("GET /path");
        hint.Should().NotContain(" — ");
    }
}
