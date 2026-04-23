using Aevatar.AI.ToolProviders.Channel;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ReplyWithInteractionToolTests
{
    [Fact]
    public async Task ExecuteAsync_captures_simple_text_into_collector()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        using var scope = collector.BeginScope();
        var tool = new ReplyWithInteractionTool(collector);

        var result = await tool.ExecuteAsync("""{"body":"hello"}""");

        result.Should().Contain("\"status\":\"queued\"");
        var captured = collector.TryTake();
        captured.Should().NotBeNull();
        captured!.Text.Should().Be("hello");
    }

    [Fact]
    public async Task ExecuteAsync_maps_title_and_body_into_intent_text()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        using var scope = collector.BeginScope();
        var tool = new ReplyWithInteractionTool(collector);

        await tool.ExecuteAsync("""{"title":"Heads up","body":"Server is rebooting."}""");

        var captured = collector.TryTake();
        captured.Should().NotBeNull();
        captured!.Text.Should().Contain("Heads up");
        captured.Text.Should().Contain("Server is rebooting.");
    }

    [Fact]
    public async Task ExecuteAsync_maps_actions_into_intent_actions()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        using var scope = collector.BeginScope();
        var tool = new ReplyWithInteractionTool(collector);

        await tool.ExecuteAsync(
            """
            {"body":"choose","actions":[
                {"action_id":"confirm","label":"Confirm","style":"primary"},
                {"action_id":"cancel","label":"Cancel"}
            ]}
            """);

        var captured = collector.TryTake();
        captured.Should().NotBeNull();
        captured!.Actions.Should().HaveCount(2);
        captured.Actions[0].ActionId.Should().Be("confirm");
        captured.Actions[0].IsPrimary.Should().BeTrue();
        captured.Actions[1].ActionId.Should().Be("cancel");
        captured.Actions[1].IsPrimary.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_maps_cards_into_intent_cards()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        using var scope = collector.BeginScope();
        var tool = new ReplyWithInteractionTool(collector);

        await tool.ExecuteAsync(
            """
            {"cards":[{
                "title":"Report",
                "text":"Daily totals",
                "fields":[{"title":"Calls","text":"42"},{"title":"Errors","text":"3"}],
                "actions":[{"action_id":"detail","label":"Open","style":"default"}]
            }]}
            """);

        var captured = collector.TryTake();
        captured.Should().NotBeNull();
        captured!.Cards.Should().HaveCount(1);
        var card = captured.Cards[0];
        card.Title.Should().Be("Report");
        card.Text.Should().Be("Daily totals");
        card.Fields.Should().HaveCount(2);
        card.Actions.Should().HaveCount(1);
        card.Actions[0].ActionId.Should().Be("detail");
    }

    [Fact]
    public async Task ExecuteAsync_rejects_empty_interaction()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        using var scope = collector.BeginScope();
        var tool = new ReplyWithInteractionTool(collector);

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("empty_interaction");
        collector.TryTake().Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_rejects_malformed_json()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        using var scope = collector.BeginScope();
        var tool = new ReplyWithInteractionTool(collector);

        var result = await tool.ExecuteAsync("{not json");

        result.Should().Contain("invalid_arguments");
    }

    [Fact]
    public async Task ExecuteAsync_without_active_scope_returns_error_not_queued_status()
    {
        var collector = new AsyncLocalInteractiveReplyCollector();
        // deliberately NO BeginScope — the tool is invoked from a non-relay turn
        var tool = new ReplyWithInteractionTool(collector);

        var result = await tool.ExecuteAsync("""{"body":"hello"}""");

        result.Should().Contain("no_active_interactive_scope");
        result.Should().NotContain("\"status\":\"queued\"");
        collector.TryTake().Should().BeNull();
    }
}
