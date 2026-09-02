using System.Linq;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Platform.Lark;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderCardFlowTests
{
    public static TheoryData<string, string, string?, string> TextAndCardToolActionCases()
    {
        return new TheoryData<string, string, string?, string>
        {
            { "/agents", "list_agents", null, "list_agents" },
            { "/agent-status skill-runner-1", "agent_status", "skill-runner-1", "agent_status" },
            { "/run-agent skill-runner-2", "run_agent", "skill-runner-2", "run_agent" },
            { "/disable-agent skill-runner-3", "disable_agent", "skill-runner-3", "disable_agent" },
            { "/enable-agent skill-runner-4", "enable_agent", "skill-runner-4", "enable_agent" },
            { "/delete-agent skill-runner-5 confirm", "delete_agent", "skill-runner-5", "delete_agent" },
        };
    }

    public static TheoryData<string, string, string> KnownTextCommandCases()
    {
        return new TheoryData<string, string, string>
        {
            { "/agents", "list_agents", "{}" },
            { "/agent-status skill-runner-1", "agent_status", "{}" },
            { "/run-agent skill-runner-2", "run_agent", "{}" },
            { "/disable-agent skill-runner-3", "disable_agent", "{}" },
            { "/enable-agent skill-runner-4", "enable_agent", "{}" },
            { "/delete-agent skill-runner-5", string.Empty, "{}" },
            { "/delete-agent skill-runner-5 confirm", "delete_agent", "{}" },
        };
    }

    public static TheoryData<string, string> FormatterCases()
    {
        return new TheoryData<string, string>
        {
            {
                "list_agents",
                """
                {
                  "agents": [
                    {
                      "agent_id": "skill-runner-list-1",
                      "template": "summary",
                      "status": "running",
                      "next_scheduled_run": "2026-04-23T09:00:00Z"
                    }
                  ]
                }
                """
            },
            {
                "agent_status",
                """
                {
                  "agent_id": "skill-runner-status-1",
                  "template": "summary",
                  "status": "running",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "last_run_at": "2026-04-25T05:30:00Z",
                  "next_scheduled_run": "2026-04-26T09:00:00Z",
                  "error_count": "0"
                }
                """
            },
            {
                "run_agent",
                """
                {
                  "agent_id": "skill-runner-run-1",
                  "template": "summary",
                  "status": "running",
                  "note": "Manual run dispatched."
                }
                """
            },
            {
                "disable_agent",
                """
                {
                  "agent_id": "skill-runner-disable-1",
                  "template": "summary",
                  "status": "disabled",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "last_run_at": "2026-04-25T05:30:00Z",
                  "next_scheduled_run": "n/a",
                  "error_count": "0"
                }
                """
            },
            {
                "enable_agent",
                """
                {
                  "agent_id": "skill-runner-enable-1",
                  "template": "summary",
                  "status": "running",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "last_run_at": "2026-04-25T05:30:00Z",
                  "next_scheduled_run": "2026-04-26T09:00:00Z",
                  "error_count": "0"
                }
                """
            },
            {
                "delete_agent",
                """
                {
                  "status": "deleted",
                  "agent_id": "skill-runner-delete-1",
                  "revoked_api_key_id": "key-1",
                  "agents": []
                }
                """
            },
        };
    }

    [Theory]
    [MemberData(nameof(TextAndCardToolActionCases))]
    public async Task TryResolveAsync_TextAndCardSpecs_MapToSameToolAction(
        string textCommand,
        string cardAction,
        string? agentId,
        string expectedAction)
    {
        var textInbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            RegistrationScopeId = "scope-1",
            Text = textCommand,
        };
        var cardInbound = new ChannelInboundEvent
        {
            ChatType = "card_action",
            RegistrationScopeId = "scope-1",
        };
        cardInbound.Extra["agent_builder_action"] = cardAction;
        if (agentId is not null)
            cardInbound.Extra["agent_id"] = agentId;

        var textDecision = await AgentBuilderCardFlow.TryResolveAsync(textInbound, userConfigQueryPort: null);
        var cardDecision = await AgentBuilderCardFlow.TryResolveAsync(cardInbound, userConfigQueryPort: null);

        textDecision.Should().NotBeNull();
        cardDecision.Should().NotBeNull();
        textDecision!.RequiresToolExecution.Should().BeTrue();
        cardDecision!.RequiresToolExecution.Should().BeTrue();
        textDecision.ToolAction.Should().Be(cardDecision.ToolAction);
        textDecision.ToolAction.Should().Be(expectedAction);

        using var textBody = JsonDocument.Parse(textDecision.ToolArgumentsJson!);
        using var cardBody = JsonDocument.Parse(cardDecision.ToolArgumentsJson!);
        textBody.RootElement.GetProperty("action").GetString().Should().Be(expectedAction);
        cardBody.RootElement.GetProperty("action").GetString().Should().Be(expectedAction);
        if (agentId is not null)
        {
            textBody.RootElement.GetProperty("agent_id").GetString().Should().Be(agentId);
            cardBody.RootElement.GetProperty("agent_id").GetString().Should().Be(agentId);
        }
    }

    [Theory]
    [MemberData(nameof(KnownTextCommandCases))]
    public async Task TryResolveAsync_AllKnownTextCommands_ArePrivateChatRestricted(
        string textCommand,
        string expectedToolAction,
        string _)
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "group",
            Text = textCommand,
        };

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyPayload.Should().Contain("private chat");
        decision.ReplyPayload.Should().Contain(textCommand.Split(' ')[0]);
        expectedToolAction.Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(FormatterCases))]
    public void FormatToolResult_KnownToolActions_UseRegisteredFormatter(string toolAction, string toolResultJson)
    {
        var decision = AgentBuilderFlowDecision.ToolCall(toolAction, JsonSerializer.Serialize(new
        {
            action = toolAction,
        }));

        var result = AgentBuilderCardFlow.FormatToolResult(decision, toolResultJson);

        result.Text.Should().BeNullOrEmpty();
        result.Cards.Should().NotBeEmpty();
        result.Cards.Select(card => card.Text).Should().NotContain(text => text.Contains("\"config\""));
    }

    [Fact]
    public void FormatToolResult_ListAgents_ReturnsStructuredCardNotJsonText()
    {
        // Issue #476 second leg: clicking the "Refresh List" card button used to dispatch the
        // list_agents tool through AgentBuilderCardFlow.FormatToolResult, which wrapped a Lark
        // card JSON string in MessageContent.Text. The relay forwarded that JSON as plain text,
        // so the user saw a raw `{"config":...}` dump alongside (or instead of) a card. The card
        // path now returns the same structured MessageContent as the typed `/agents` flow so the
        // composer can render it as a single native card, with no JSON-shaped Text body.
        var decision = AgentBuilderFlowDecision.ToolCall("list_agents", """{"action":"list_agents"}""");
        var result = AgentBuilderCardFlow.FormatToolResult(
            decision,
            """
            {
              "agents": [
                {
                  "agent_id": "skill-runner-card-click-1",
                  "template": "summary",
                  "status": "running",
                  "next_scheduled_run": "2026-04-23T09:00:00Z"
                }
              ]
            }
            """);

        result.Text.Should().BeNullOrEmpty();
        result.Cards.Should().ContainSingle(card => card.BlockId == "agents_list");
        result.Cards.Single().Title.Should().Be("Your Agents (1)");
        result.Cards.Single().Text.Should().Contain("skill-runner-card-click-1");
        // No raw Lark JSON envelope leaks into the body.
        result.Cards.Single().Text.Should().NotContain("\"config\"");
        result.Cards.Single().Text.Should().NotContain("\"elements\"");
    }

    [Fact]
    public void FormatToolResult_ListAgents_ReturnsSingleCardWithoutPerAgentButtons()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("list_agents", """{"action":"list_agents"}""");
        var result = AgentBuilderCardFlow.FormatToolResult(
            decision,
            """
            {
              "agents": [
                {
                  "agent_id": "skill-runner-94d754dfdfbb416aa5a676cecd0d7a71",
                  "template": "legacy-template",
                  "status": "running",
                  "output_format": "text",
                  "next_scheduled_run": "2026-04-23T09:00:00Z",
                  "last_run_at": "2026-04-22T09:00:00Z"
                }
              ]
            }
            """);

        result.Cards.Should().ContainSingle();
        var card = result.Cards.Single();
        card.BlockId.Should().Be("agents_list");
        card.Title.Should().Be("Your Agents (1)");
        card.Text.Should().Contain("legacy-template");
        card.Text.Should().Contain("skill-runner-94d754dfdfbb416aa5a676cecd0d7a71");
        card.Text.Should().Contain("running");
        card.Text.Should().Contain("Output: `text`");
        card.Text.Should().Contain("/agent-status <id>");
        card.Text.Should().Contain("/run-agent <id>");
        card.Text.Should().Contain("/delete-agent <id> confirm");

        result.Actions.Should().NotContain(a => a.ActionId == "agent_status");
        result.Actions.Should().NotContain(a => a.Arguments.ContainsKey("agent_id"));
        result.Actions.Select(a => a.ActionId).Should().BeEquivalentTo(new[] { "list_agents" });
    }

    [Theory]
    [InlineData("/agent-status skill-runner-1", "agent_status", "skill-runner-1")]
    [InlineData("/run-agent skill-runner-2", "run_agent", "skill-runner-2")]
    [InlineData("/disable-agent skill-runner-3", "disable_agent", "skill-runner-3")]
    [InlineData("/enable-agent skill-runner-4", "enable_agent", "skill-runner-4")]
    public async Task TryResolveAsync_SimpleAgentTextCommand_ReturnsToolCall(
        string text,
        string expectedAction,
        string expectedAgentId)
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            RegistrationScopeId = "scope-1",
            Text = text,
        };

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeTrue();
        decision.ToolAction.Should().Be(expectedAction);

        using var body = JsonDocument.Parse(decision.ToolArgumentsJson!);
        body.RootElement.GetProperty("action").GetString().Should().Be(expectedAction);
        body.RootElement.GetProperty("agent_id").GetString().Should().Be(expectedAgentId);
    }

    [Fact]
    public async Task TryResolveAsync_SimpleAgentTextCommand_WithoutAgentId_ReturnsUsage()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = "/agent-status",
        };

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyPayload.Should().Be("Usage: /agent-status <agent_id>");
    }

    [Fact]
    public void FormatToolResult_DeleteAgent_RendersUpdatedListWithNotice()
    {
        // After a delete completes, the user should see (a) confirmation that the right agent
        // is gone and (b) the remaining registry inline, not the legacy multi-card layout the
        // composer previously emitted via BuildAgentListCard's raw Lark JSON.
        var decision = AgentBuilderFlowDecision.ToolCall("delete_agent", """{"action":"delete_agent"}""");
        var result = AgentBuilderCardFlow.FormatToolResult(
            decision,
            """
            {
              "status": "deleted",
              "agent_id": "skill-runner-deleted-1",
              "revoked_api_key_id": "key-1",
              "agents": [
                {
                  "agent_id": "skill-runner-remaining-1",
                  "template": "social_media",
                  "status": "running",
                  "next_scheduled_run": "2026-04-23T09:00:00Z"
                }
              ]
            }
            """);

        result.Text.Should().BeNullOrEmpty();
        result.Cards.Should().ContainSingle(card => card.BlockId == "agents_list");
        var card = result.Cards.Single();
        // Notice + the (still-present) remaining agent are both visible in the same card body.
        card.Text.Should().Contain("Deleted agent `skill-runner-deleted-1`");
        card.Text.Should().Contain("skill-runner-remaining-1");
    }

    [Fact]
    public void FormatToolResult_AgentStatus_ReturnsStructuredCardWithLifecycleButtons()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("agent_status", """{"action":"agent_status"}""");
        var result = AgentBuilderCardFlow.FormatToolResult(
            decision,
            """
            {
              "agent_id": "skill-runner-1",
              "template": "summary",
              "status": "running",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "UTC",
              "output_format": "feishu_doc",
              "last_run_at": "2026-04-25T05:30:00Z",
              "next_scheduled_run": "2026-04-26T09:00:00Z",
              "error_count": "0"
            }
            """);

        result.Text.Should().BeNullOrEmpty();
        result.Cards.Should().ContainSingle(card => card.BlockId == "agent_status:skill-runner-1");
        result.Cards.Single().Text.Should().Contain("Status: `running`");
        result.Cards.Single().Text.Should().Contain("Output: `feishu_doc`");
        // Lifecycle buttons render as actions, not as embedded JSON in MessageContent.Text.
        result.Actions.Should().Contain(a => a.ActionId == "run_agent");
        result.Actions.Should().Contain(a => a.ActionId == "disable_agent");
        // `confirm_delete_agent` so the card-flow path keeps the explicit confirmation step.
        var deleteButton = result.Actions.Should().Contain(a => a.ActionId == "confirm_delete_agent").Subject;
        deleteButton.IsDanger.Should().BeTrue();
        deleteButton.Arguments.Should().Contain(new KeyValuePair<string, string>("agent_id", "skill-runner-1"));
        deleteButton.Arguments.Should().Contain(new KeyValuePair<string, string>("template", "summary"));
    }

    [Fact]
    public void FormatToolResult_RunAgent_ReturnsStructuredCardNotJsonText()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("run_agent", """{"action":"run_agent"}""");
        var result = AgentBuilderCardFlow.FormatToolResult(
            decision,
            """
            {
              "agent_id": "skill-runner-1",
              "template": "summary",
              "status": "running",
              "note": "Manual run dispatched."
            }
            """);

        result.Text.Should().BeNullOrEmpty();
        result.Cards.Should().ContainSingle(card => card.BlockId == "run_triggered:skill-runner-1");
        result.Cards.Single().Text.Should().Contain("Manual run dispatched");
        result.Cards.Single().Text.Should().NotContain("\"config\"");
        result.Actions.Should().Contain(a => a.ActionId == "list_agents");
        var refreshButton = result.Actions.Should()
            .Contain(a => a.ActionId == "agent_status").Subject;
        refreshButton.Arguments.Should().Contain(new KeyValuePair<string, string>(
            "agent_id", "skill-runner-1"));
    }

    [Fact]
    public async Task TryResolveAsync_DeleteAgentTextCommand_TreatsConfirmTrailerAsExplicitConfirmation()
    {
        // Review feedback on PR #483: the shared `/agents` renderer prints the inline command
        // hint `/delete-agent <id> confirm` (matching the NyxRelay text contract). The direct
        // CardFlow text-command parser used to take everything after the command as the agent_id,
        // so following that hint produced an agent_id of `<id> confirm` and the confirmation card
        // built for a bogus id whose Confirm Delete button then failed the actual delete. This
        // test pins the new behavior: the trailing `confirm` token is recognized and the parser
        // dispatches the delete directly (matching NyxRelay's semantics) instead of stuffing it
        // into the agent_id.
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            RegistrationScopeId = "scope-1",
            Text = "/delete-agent skill-runner-1 confirm",
        };

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeTrue();
        decision.ToolAction.Should().Be("delete_agent");

        using var body = JsonDocument.Parse(decision.ToolArgumentsJson!);
        body.RootElement.GetProperty("action").GetString().Should().Be("delete_agent");
        body.RootElement.GetProperty("agent_id").GetString().Should().Be("skill-runner-1");
        body.RootElement.GetProperty("confirm").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TryResolveAsync_DeleteAgentTextCommand_WithoutConfirmTrailer_StillShowsConfirmationCard()
    {
        // Without the explicit `confirm` trailer the direct CardFlow path keeps the existing
        // UI-driven confirmation step (the user clicks Confirm Delete) — the new parser must
        // not change that behavior, only handle the new trailer correctly.
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            RegistrationScopeId = "scope-1",
            Text = "/delete-agent skill-runner-1",
        };

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyContent.Should().NotBeNull();
        decision.ReplyContent!.Cards.Should().ContainSingle(card =>
            card.BlockId == "delete_confirm:skill-runner-1");
    }

    [Fact]
    public async Task TryResolveAsync_DeleteAgentTextCommand_WithoutAgentId_ReturnsUsage()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = "/delete-agent",
        };

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyPayload.Should().Be("Usage: /delete-agent <agent_id>");
    }

    [Theory]
    [InlineData("/foobar")]
    [InlineData("/goal draft Q2 launch")]
    [InlineData("/summary")]
    [InlineData("/summary alice")]
    [InlineData("/SUMMARY alice schedule_time=09:00")]
    public async Task TryResolveAsync_UnknownAndOrnnSlashCommands_FallThrough(string text)
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = text,
        };

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().BeNull();
    }

    [Fact]
    public async Task TryResolveAsync_KnownTextCommandInGroup_ReturnsPrivateChatRestriction()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "group",
            Text = "/agents",
        };

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyPayload.Should().Contain("private chat");
        decision.ReplyPayload.Should().Contain("/agents");
    }

    [Theory]
    [InlineData("hello there")]
    [InlineData("现在就是私聊")]
    [InlineData("   ")]
    public async Task TryResolveAsync_NonSlashText_FallsThrough(string text)
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = text,
        };

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().BeNull();
    }

    [Fact]
    public async Task TryResolveAsync_ConfirmDeleteAgent_ReturnsStructuredCardNotJsonText()
    {
        // Pre-fix this branch returned DirectReply with a Lark card JSON string in ReplyPayload
        // (no ReplyContent). The runner wrapped that string into MessageContent.Text and the
        // relay forwarded raw JSON to the user (issue #482).
        var inbound = new ChannelInboundEvent
        {
            ChatType = "card_action",
            RegistrationScopeId = "scope-1",
        };
        inbound.Extra["agent_builder_action"] = "confirm_delete_agent";
        inbound.Extra["agent_id"] = "skill-runner-1";
        inbound.Extra["template"] = "summary";

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyContent.Should().NotBeNull();
        decision.ReplyContent!.Text.Should().BeNullOrEmpty();
        decision.ReplyContent.Cards.Should().ContainSingle(card =>
            card.BlockId == "delete_confirm:skill-runner-1");
        decision.ReplyContent.Cards.Single().Text.Should().Contain("summary");
        var confirmButton = decision.ReplyContent.Actions.Should()
            .Contain(a => a.ActionId == "delete_agent").Subject;
        confirmButton.IsDanger.Should().BeTrue();
        confirmButton.Arguments.Should().Contain(new KeyValuePair<string, string>(
            "agent_id", "skill-runner-1"));
        decision.ReplyContent.Actions.Should().Contain(a => a.ActionId == "list_agents");
    }

}
