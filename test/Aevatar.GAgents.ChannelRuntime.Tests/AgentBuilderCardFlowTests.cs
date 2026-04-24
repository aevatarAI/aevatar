using System.Linq;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Xunit;
using StudioUserConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderCardFlowTests
{
    [Fact]
    public async Task TryResolveAsync_DailyReportLaunch_PrefillsSavedGithubUsername()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            RegistrationScopeId = "scope-1",
            Text = "/daily",
        };

        var decision = await AgentBuilderCardFlow.TryResolveAsync(
            inbound,
            new StubUserConfigQueryPort(new StudioUserConfig(DefaultModel: string.Empty, GithubUsername: "saved-user")));

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyContent.Should().NotBeNull();

        var githubInput = decision.ReplyContent!.Actions.Single(a =>
            a.Kind == ActionElementKind.TextInput && a.ActionId == "github_username");
        // Saved usernames belong in Value (rendered as default_value) so the user sees editable text
        // rather than placeholder ghost text that disappears on click.
        githubInput.Value.Should().Be("saved-user");

        decision.ReplyContent.Cards.Single().Text.Should().Contain("saved-user");
        decision.ReplyContent.Cards.Single().Text.Should().Contain("already filled in");
    }

    [Fact]
    public async Task TryResolveAsync_DailyReportSubmit_AllowsMissingGithubUsername_ForUserConfigFallback()
    {
        var inbound = new ChannelInboundEvent
        {
            ChatType = "card_action",
            RegistrationScopeId = "scope-1",
        };
        inbound.Extra["agent_builder_action"] = "create_daily_report";
        inbound.Extra["schedule_time"] = "09:00";

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeTrue();

        using var body = JsonDocument.Parse(decision.ToolArgumentsJson!);
        body.RootElement.GetProperty("action").GetString().Should().Be("create_agent");
        body.RootElement.GetProperty("template").GetString().Should().Be("daily_report");
        body.RootElement.GetProperty("schedule_cron").GetString().Should().Be("0 9 * * *");
        body.RootElement.GetProperty("github_username").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private sealed class StubUserConfigQueryPort : IUserConfigQueryPort
    {
        private readonly StudioUserConfig _config;

        public StubUserConfigQueryPort(StudioUserConfig config)
        {
            _config = config;
        }

        public Task<StudioUserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(_config);

        public Task<StudioUserConfig> GetAsync(string scopeId, CancellationToken ct = default) => Task.FromResult(_config);
    }
}
