using System.Linq;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxRelayAgentBuilderFlowTests
{
    [Fact]
    public void TryResolve_ShouldBuildDailyReportToolCall_ForDailyWithoutArguments()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            ConversationId = "oc_default_daily",
            Text = "/daily",
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeTrue();
        decision.ToolAction.Should().Be("create_daily_report");

        using var body = JsonDocument.Parse(decision.ToolArgumentsJson!);
        body.RootElement.GetProperty("action").GetString().Should().Be("create_agent");
        body.RootElement.GetProperty("template").GetString().Should().Be("daily_report");
        body.RootElement.GetProperty("github_username").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("schedule_cron").GetString().Should().Be("0 9 * * *");
        body.RootElement.GetProperty("conversation_id").GetString().Should().Be("oc_default_daily");
    }

    [Fact]
    public void TryResolve_ShouldAcceptPositionalGithubUsername_AndForwardConversationId()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            ConversationId = "oc_8a70aeefbdb4340e1fa5f575b4c794eb",
            Text = "/daily eanzhao",
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeTrue();
        decision.ToolAction.Should().Be("create_daily_report");

        using var body = JsonDocument.Parse(decision.ToolArgumentsJson!);
        body.RootElement.GetProperty("action").GetString().Should().Be("create_agent");
        body.RootElement.GetProperty("template").GetString().Should().Be("daily_report");
        body.RootElement.GetProperty("github_username").GetString().Should().Be("eanzhao");
        body.RootElement.GetProperty("save_github_username_preference").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("run_immediately").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("conversation_id").GetString().Should().Be("oc_8a70aeefbdb4340e1fa5f575b4c794eb");
    }

    [Fact]
    public void TryResolve_ShouldNotRequestPreferenceSave_WhenDailyHasNoUsername()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            ConversationId = "oc_default_daily",
            Text = "/daily",
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeTrue();

        using var body = JsonDocument.Parse(decision.ToolArgumentsJson!);
        body.RootElement.GetProperty("github_username").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("save_github_username_preference").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("/daily =broken")]
    [InlineData("/daily github_username=")]
    public void TryResolve_ShouldPassThroughNullGithubUsername_WhenMissingOrEmpty(string text)
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            ConversationId = "oc_chat_xyz",
            Text = text,
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeTrue();
        decision.ToolAction.Should().Be("create_daily_report");

        using var body = JsonDocument.Parse(decision.ToolArgumentsJson!);
        body.RootElement.GetProperty("github_username").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("schedule_cron").GetString().Should().Be("0 9 * * *");
    }

    [Fact]
    public void TryResolve_ShouldAcceptPositionalSocialMediaTopic()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            ConversationId = "oc_chat_abc",
            Text = "/social-media \"Launch update\" schedule_time=10:30",
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeTrue();
        decision.ToolAction.Should().Be("create_social_media");

        using var body = JsonDocument.Parse(decision.ToolArgumentsJson!);
        body.RootElement.GetProperty("topic").GetString().Should().Be("Launch update");
        body.RootElement.GetProperty("schedule_cron").GetString().Should().Be("30 10 * * *");
        body.RootElement.GetProperty("conversation_id").GetString().Should().Be("oc_chat_abc");
    }

    [Fact]
    public void TryResolve_ShouldBuildCreateSocialMediaToolCall_FromTextCommand()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = "/social-media topic=\"Launch update\" schedule_time=10:30 audience=\"Developers\" style=\"Confident\"",
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeTrue();
        decision.ToolAction.Should().Be("create_social_media");

        using var body = JsonDocument.Parse(decision.ToolArgumentsJson!);
        body.RootElement.GetProperty("action").GetString().Should().Be("create_agent");
        body.RootElement.GetProperty("template").GetString().Should().Be("social_media");
        body.RootElement.GetProperty("topic").GetString().Should().Be("Launch update");
        body.RootElement.GetProperty("schedule_cron").GetString().Should().Be("30 10 * * *");
        body.RootElement.GetProperty("audience").GetString().Should().Be("Developers");
    }

    [Fact]
    public void FormatToolResult_ShouldRenderListAgents_AsSingleCardWithoutPerAgentButtons()
    {
        // Issue #476: /agents used to render as one summary card + N per-agent cards + N
        // "Status: …" per-agent buttons. In Lark that compiled into stacked markdown blocks plus
        // a long button row, which users perceived as a text list mixed with a separate status
        // card. The unified design surfaces ONE card with a structured agent list in the body,
        // a small footer of global actions, and the per-agent operations as documented slash
        // commands inline in the body.
        var decision = AgentBuilderFlowDecision.ToolCall("list_agents", """{"action":"list_agents"}""");
        var result = NyxRelayAgentBuilderFlow.FormatToolResult(
            decision,
            """
            {
              "agents": [
                {
                  "agent_id": "skill-runner-94d754dfdfbb416aa5a676cecd0d7a71",
                  "template": "daily_report",
                  "status": "running",
                  "next_scheduled_run": "2026-04-23T09:00:00Z",
                  "last_run_at": "2026-04-22T09:00:00Z"
                },
                {
                  "agent_id": "skill-runner-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d",
                  "template": "social_media",
                  "status": "disabled",
                  "next_scheduled_run": "pending"
                }
              ]
            }
            """);

        // Single consolidated card — no per-agent CardBlock rows, no `agents_summary` extra block.
        result.Cards.Should().ContainSingle();
        var card = result.Cards.Single();
        card.BlockId.Should().Be("agents_list");
        card.Title.Should().Be("Your Agents (2)");
        // Body lists every agent with its identifying fields in markdown.
        card.Text.Should().Contain("daily_report");
        card.Text.Should().Contain("skill-runner-94d754dfdfbb416aa5a676cecd0d7a71");
        card.Text.Should().Contain("running");
        card.Text.Should().Contain("social_media");
        card.Text.Should().Contain("disabled");
        // Per-agent commands live in the body so users do not have to remember them.
        card.Text.Should().Contain("/agent-status <id>");
        card.Text.Should().Contain("/run-agent <id>");
        card.Text.Should().Contain("/delete-agent <id> confirm");

        // No per-agent buttons. Specifically no `agent_status` action with an agent_id argument
        // — that was the source of the long "Status: …" row that read as a separate panel.
        result.Actions.Should().NotContain(a => a.ActionId == "agent_status");
        result.Actions.Should().NotContain(a => a.Arguments.ContainsKey("agent_id"));

        // Footer keeps four global discovery / creation buttons in a single row.
        result.Actions.Select(a => a.ActionId).Should().BeEquivalentTo(new[]
        {
            "list_agents",
            "list_templates",
            "open_daily_report_form",
            "open_social_media_form",
        });
    }

    [Fact]
    public void FormatToolResult_ShouldRenderEmptyListAgentsAsCallToActionCard()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("list_agents", """{"action":"list_agents"}""");
        var result = NyxRelayAgentBuilderFlow.FormatToolResult(decision, """{"agents":[]}""");

        result.Cards.Should().ContainSingle(card => card.BlockId == "agents_empty");
        result.Actions.Should().Contain(a => a.ActionId == "open_daily_report_form");
        result.Actions.Should().Contain(a => a.ActionId == "open_social_media_form");
        result.Actions.Should().Contain(a => a.ActionId == "list_templates");
    }

    [Fact]
    public void FormatToolResult_ShouldRenderAgentStatusAsInteractiveCard_WithLifecycleButtons()
    {
        // /agent-status now ships as an interactive card with one button per lifecycle action
        // (Run, Disable, Enable, Delete) so the user does not have to retype the agent_id for
        // each follow-up command. Each button carries the agent_id in its arguments and the
        // delete button additionally carries `confirm=true` so AgentBuilderCardFlow's existing
        // confirm-required handler skips the second-step prompt.
        var decision = AgentBuilderFlowDecision.ToolCall("agent_status", """{"action":"agent_status"}""");
        var result = NyxRelayAgentBuilderFlow.FormatToolResult(
            decision,
            """
            {
              "agent_id": "skill-runner-1",
              "template": "daily_report",
              "status": "error",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "UTC",
              "last_run_at": "2026-04-25T05:30:00Z",
              "next_scheduled_run": "2026-04-26T09:00:00Z",
              "last_error": "Lark message delivery rejected"
            }
            """);

        result.Cards.Should().ContainSingle(card => card.BlockId == "agent_status:skill-runner-1");
        result.Cards[0].Text.Should().Contain("Status: `error`");
        result.Cards[0].Text.Should().Contain("Last error:");

        result.Actions.Should().Contain(a => a.ActionId == "run_agent");
        result.Actions.Should().Contain(a => a.ActionId == "disable_agent");
        result.Actions.Should().Contain(a => a.ActionId == "enable_agent");
        result.Actions.Should().Contain(a => a.ActionId == "list_agents");

        var deleteButton = result.Actions.Should().Contain(a => a.ActionId == "delete_agent").Subject;
        deleteButton.IsDanger.Should().BeTrue();
        deleteButton.Arguments.Should().Contain(new KeyValuePair<string, string>("confirm", "true"));
        deleteButton.Arguments.Should().Contain(new KeyValuePair<string, string>("agent_id", "skill-runner-1"));
    }

    [Fact]
    public void FormatToolResult_ShouldRenderAgentStatusError_WhenToolReturnsError()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("agent_status", """{"action":"agent_status"}""");
        var result = NyxRelayAgentBuilderFlow.FormatToolResult(
            decision,
            """{"error":"Agent not found"}""");

        result.Cards.Should().BeEmpty();
        result.Actions.Should().BeEmpty();
        result.Text.Should().Contain("Agent status failed: Agent not found");
    }

    [Fact]
    public void TryResolve_ShouldRequireDeleteConfirmation()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = "/delete-agent agent-1",
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyPayload.Should().Contain("/delete-agent agent-1 confirm");
    }

    [Theory]
    [InlineData("/daily_report alice", "Unknown command: /daily_report")]
    [InlineData("/foobar", "Unknown command: /foobar")]
    [InlineData("/", "Unknown command: /")]
    public void TryResolve_ShouldReturnUnknownCommandUsage_ForUnknownSlash(string text, string expected)
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = text,
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyPayload.Should().Contain(expected);
        decision.ReplyPayload.Should().Contain("/daily [github_username]");
    }

    [Fact]
    public void TryResolve_ShouldMergeSlashRegistryDescriptors_ForUnknownSlash()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = "/foobar",
        };
        var registry = new ChannelSlashCommandRegistry(new IChannelSlashCommandHandler[]
        {
            new StubSlashHandler(new ChannelSlashCommandUsage("init", string.Empty, "Bind NyxID")),
            new StubSlashHandler(new ChannelSlashCommandUsage("model", "use <service-number|model-name>", "Pick LLM")),
        });

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision, registry);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.ReplyPayload.Should().Contain("/init");
        decision.ReplyPayload.Should().Contain("/model use <service-number|model-name>");
    }

    [Fact]
    public void TryResolve_ShouldReturnPrivateChatRestriction_ForKnownCommandInGroup()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "group",
            Text = "/daily alice",
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyPayload.Should().Contain("private chat");
        decision.ReplyPayload.Should().Contain("/daily");
    }

    [Theory]
    [InlineData("hello there")]
    [InlineData("现在就是私聊")]
    public void TryResolve_ShouldFallThrough_ForNonSlashText(string text)
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = text,
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeFalse();
        decision.Should().BeNull();
    }

    [Fact]
    public void TryResolve_ShouldFallThrough_ForEmptyText()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = "   ",
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeFalse();
        decision.Should().BeNull();
    }

    [Fact]
    public void FormatToolResult_ShouldReturnCardForm_WhenCredentialsRequired()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("create_daily_report", "{}");
        var toolResultJson = JsonSerializer.Serialize(new
        {
            status = "credentials_required",
            template = "daily_report",
            provider_id = "p-github",
            note = "Could not resolve github_username. Provide github_username explicitly, save a default preference, or reconnect GitHub in NyxID.",
        });

        var result = NyxRelayAgentBuilderFlow.FormatToolResult(decision, toolResultJson);

        result.Actions.Should().NotBeEmpty();
        result.Actions.Any(action => action.Kind == ActionElementKind.TextInput && action.ActionId == "github_username")
            .Should().BeTrue();
        result.Actions.Any(action => action.Kind == ActionElementKind.FormSubmit && action.ActionId == "submit_daily_report")
            .Should().BeTrue();
        result.Cards.Should().HaveCount(1);
        result.Cards[0].Title.Should().Be("Create Daily Report Agent");
        result.Cards[0].Text.Should().Contain("GitHub credentials required");
        result.Cards[0].Text.Should().Contain("p-github");
        // The auth body lives in the card only — content.Text must stay empty so Lark's form-mode
        // composer (LarkMessageComposer.BuildLeadingMarkdown) doesn't double-render the same
        // "GitHub credentials required" block once from Text and once from the card body. The
        // earlier assertion that Text was non-empty was codifying the bug it has since fixed.
        result.Text.Should().BeEmpty();
    }

    [Fact]
    public void FormatToolResult_ShouldAckImmediateRun_WithSavedPreference()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("create_daily_report", "{}");
        var toolResultJson = JsonSerializer.Serialize(new
        {
            status = "created",
            agent_id = "skill-runner-1ba2e9f3",
            agent_type = "skill_runner",
            template = "daily_report",
            github_username = "eanzhao",
            github_username_preference_saved = true,
            run_immediately_requested = true,
            next_scheduled_run = "2026-04-25T09:00:00+00:00",
            conversation_id = "oc_default_daily",
        });

        var result = NyxRelayAgentBuilderFlow.FormatToolResult(decision, toolResultJson);

        result.Actions.Should().BeEmpty();
        result.Cards.Should().BeEmpty();
        result.Text.Should().Contain("Daily report scheduled for `eanzhao`");
        result.Text.Should().Contain("Running first report now");
        result.Text.Should().Contain("I'll reply with the results shortly");
        result.Text.Should().Contain("Saved `eanzhao` as your default GitHub username");
        result.Text.Should().Contain("Next scheduled run: 2026-04-25T09:00:00+00:00");
        result.Text.Should().Contain("skill-runner-1ba2e9f3");
    }

    [Fact]
    public void FormatToolResult_ShouldNotMentionSavedPreference_WhenSaveNotRequested()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("create_daily_report", "{}");
        var toolResultJson = JsonSerializer.Serialize(new
        {
            status = "created",
            agent_id = "skill-runner-1",
            template = "daily_report",
            github_username = "eanzhao",
            github_username_preference_saved = false,
            run_immediately_requested = true,
            next_scheduled_run = "2026-04-25T09:00:00+00:00",
        });

        var result = NyxRelayAgentBuilderFlow.FormatToolResult(decision, toolResultJson);

        result.Text.Should().Contain("Daily report scheduled for `eanzhao`");
        result.Text.Should().Contain("Running first report now");
        result.Text.Should().NotContain("as your default GitHub username");
    }

    private sealed class StubSlashHandler(ChannelSlashCommandUsage usage) : IChannelSlashCommandHandler
    {
        public string Name => usage.Name;
        public bool RequiresBinding => false;
        public ChannelSlashCommandUsage Usage => usage;

        public Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct) =>
            Task.FromResult<MessageContent?>(null);
    }
}
