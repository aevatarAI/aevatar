using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxRelayAgentBuilderFlowTests
{
    [Fact]
    public void TryResolve_ShouldReturnHelp_ForDailyReportWithoutArguments()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            Text = "/daily-report",
        };

        var matched = NyxRelayAgentBuilderFlow.TryResolve(inbound, out var decision);

        matched.Should().BeTrue();
        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyPayload.Should().Contain("Daily report agent command");
        decision.ReplyPayload.Should().Contain("/daily-report github_username=alice");
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
}
