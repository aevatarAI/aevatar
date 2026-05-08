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
                  "template": "legacy-template",
                  "status": "running",
                  "next_scheduled_run": "2026-04-23T09:00:00Z",
                  "last_run_at": "2026-04-22T09:00:00Z"
                }
              ]
            }
            """);

        // Single consolidated card — no per-agent CardBlock rows, no `agents_summary` extra block.
        result.Cards.Should().ContainSingle();
        var card = result.Cards.Single();
        card.BlockId.Should().Be("agents_list");
        card.Title.Should().Be("Your Agents (1)");
        // Body lists every agent with its identifying fields in markdown.
        card.Text.Should().Contain("legacy-template");
        card.Text.Should().Contain("skill-runner-94d754dfdfbb416aa5a676cecd0d7a71");
        card.Text.Should().Contain("running");
        // Per-agent commands live in the body so users do not have to remember them.
        card.Text.Should().Contain("/agent-status <id>");
        card.Text.Should().Contain("/run-agent <id>");
        card.Text.Should().Contain("/delete-agent <id> confirm");

        // No per-agent buttons. Specifically no `agent_status` action with an agent_id argument
        // — that was the source of the long "Status: …" row that read as a separate panel.
        result.Actions.Should().NotContain(a => a.ActionId == "agent_status");
        result.Actions.Should().NotContain(a => a.Arguments.ContainsKey("agent_id"));

        // Footer is now just a single Refresh button — there are no in-tree creation flows;
        // recipes for new agents come from Ornn skills (issue #598).
        result.Actions.Select(a => a.ActionId).Should().BeEquivalentTo(new[] { "list_agents" });
    }

    [Fact]
    public void FormatToolResult_ShouldRenderEmptyListAgentsAsCallToActionCard()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("list_agents", """{"action":"list_agents"}""");
        var result = NyxRelayAgentBuilderFlow.FormatToolResult(decision, """{"agents":[]}""");

        result.Cards.Should().ContainSingle(card => card.BlockId == "agents_empty");
        result.Actions.Should().Contain(a => a.ActionId == "list_agents");
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
              "template": "daily",
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
        decision.ReplyPayload.Should().Contain("/agents");
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
            Text = "/agents",
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyPayload.Should().Contain("private chat");
        decision.ReplyPayload.Should().Contain("/agents");
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

    private sealed class StubSlashHandler(ChannelSlashCommandUsage usage) : IChannelSlashCommandHandler
    {
        public string Name => usage.Name;
        public bool RequiresBinding => false;
        public ChannelSlashCommandUsage Usage => usage;

        public Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct) =>
            Task.FromResult<MessageContent?>(null);
    }
}
