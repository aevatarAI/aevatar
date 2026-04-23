using System.Text.Json;
using FluentAssertions;
using Xunit;

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
        body.RootElement.GetProperty("conversation_id").GetString().Should().Be("oc_8a70aeefbdb4340e1fa5f575b4c794eb");
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
    public void FormatToolResult_ShouldRenderPlainTextListAgentsResponse()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("list_agents", """{"action":"list_agents"}""");
        var result = NyxRelayAgentBuilderFlow.FormatToolResult(
            decision,
            """
            {
              "agents": [
                {
                  "agent_id": "agent-1",
                  "template": "daily_report",
                  "status": "running",
                  "next_scheduled_run": "2026-04-23T09:00:00Z"
                }
              ]
            }
            """);

        result.Should().Contain("Current agents:");
        result.Should().Contain("agent-1: template=daily_report, status=running");
        result.Should().Contain("/agent-status <agent_id>");
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
}
