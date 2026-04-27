using System.Linq;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Authoring;
using Aevatar.GAgents.Channel.Runtime;
using StudioUserConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderCardFlowTests
{
    [Fact]
    public async Task TryResolveAsync_DailyReportLaunch_PrefillsSavedGithubUsername()
    {
        // Inbound carries Platform + SenderId so the prefill query must hit the per-user
        // scope (`scope-1:lark:ou_alice`), not the bot-level `scope-1` — otherwise multiple
        // Lark users sharing a bot would see each other's saved usernames (issue #436).
        var inbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            RegistrationScopeId = "scope-1",
            Platform = "lark",
            SenderId = "ou_alice",
            Text = "/daily",
        };
        var queryPort = new MapStubUserConfigQueryPort();
        queryPort.SetGithubUsername("scope-1:lark:ou_alice", "saved-user");

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, queryPort);

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
    public async Task TryResolveAsync_DailyReportLaunch_TwoLarkUsersInSameBot_SeeIndependentSavedUsernames()
    {
        // Issue #436: when colleagues share one Lark bot, the prefill must read each
        // sender's own saved github_username — not the most recent writer's value.
        // Pin that the per-user scope (`{bot}:{platform}:{sender}`) is what reaches the
        // query port, so the read isn't accidentally collapsed back to the bot scope.
        var queryPort = new MapStubUserConfigQueryPort();
        queryPort.SetGithubUsername("scope-1:lark:ou_alice", "alice");
        queryPort.SetGithubUsername("scope-1:lark:ou_bob", "bob");

        var aliceInbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            RegistrationScopeId = "scope-1",
            Platform = "lark",
            SenderId = "ou_alice",
            Text = "/daily",
        };
        var bobInbound = new ChannelInboundEvent
        {
            ChatType = "p2p",
            RegistrationScopeId = "scope-1",
            Platform = "lark",
            SenderId = "ou_bob",
            Text = "/daily",
        };

        var aliceDecision = await AgentBuilderCardFlow.TryResolveAsync(aliceInbound, queryPort);
        var bobDecision = await AgentBuilderCardFlow.TryResolveAsync(bobInbound, queryPort);

        aliceDecision!.ReplyContent!.Actions
            .Single(a => a.Kind == ActionElementKind.TextInput && a.ActionId == "github_username")
            .Value.Should().Be("alice");
        bobDecision!.ReplyContent!.Actions
            .Single(a => a.Kind == ActionElementKind.TextInput && a.ActionId == "github_username")
            .Value.Should().Be("bob");

        queryPort.QueriedScopes.Should().BeEquivalentTo(new[]
        {
            "scope-1:lark:ou_alice",
            "scope-1:lark:ou_bob",
        });
    }

    [Fact]
    public async Task TryResolveAsync_TemplatesCardButton_DispatchesListTemplatesTool()
    {
        // The /agents card surfaces a `Templates` button; PR #409 added it. Without an explicit
        // case in the card_action switch the button click would no-op and confuse users who
        // navigate by tapping rather than typing /templates. Pin the contract so a refactor can
        // not silently drop the routing.
        var inbound = new ChannelInboundEvent
        {
            ChatType = "card_action",
            RegistrationScopeId = "scope-1",
        };
        inbound.Extra["agent_builder_action"] = "list_templates";

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeTrue();
        decision.ToolAction.Should().Be("list_templates");

        using var body = JsonDocument.Parse(decision.ToolArgumentsJson!);
        body.RootElement.GetProperty("action").GetString().Should().Be("list_templates");
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

    private sealed class MapStubUserConfigQueryPort : IUserConfigQueryPort
    {
        private readonly Dictionary<string, StudioUserConfig> _byScope = new(StringComparer.Ordinal);
        private readonly List<string> _queriedScopes = new();

        public IReadOnlyList<string> QueriedScopes => _queriedScopes;

        public void SetGithubUsername(string scopeId, string githubUsername)
        {
            _byScope[scopeId] = new StudioUserConfig(DefaultModel: string.Empty, GithubUsername: githubUsername);
        }

        public Task<StudioUserConfig> GetAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("Channel paths must call GetAsync(scopeId).");

        public Task<StudioUserConfig> GetAsync(string scopeId, CancellationToken ct = default)
        {
            _queriedScopes.Add(scopeId);
            return Task.FromResult(_byScope.TryGetValue(scopeId, out var config)
                ? config
                : new StudioUserConfig(DefaultModel: string.Empty, GithubUsername: null));
        }
    }
}
