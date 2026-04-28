using System.Linq;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Authoring.Lark;
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
                  "template": "daily_report",
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
    public void FormatToolResult_ListTemplates_ReturnsStructuredCardNotJsonText()
    {
        // Issue #482: clicking the `Templates` button used to dispatch list_templates and the
        // formatter wrapped a Lark card JSON envelope in MessageContent.Text, which the relay
        // then forwarded as raw text. Pin the structured-MessageContent contract here.
        var decision = AgentBuilderFlowDecision.ToolCall("list_templates", """{"action":"list_templates"}""");
        var result = AgentBuilderCardFlow.FormatToolResult(
            decision,
            """
            {
              "templates": [
                {
                  "name": "daily_report",
                  "status": "ready",
                  "description": "Daily GitHub report.",
                  "required_fields": ["github_username"],
                  "optional_fields": ["repositories", "schedule_time"]
                },
                {
                  "name": "social_media",
                  "status": "ready",
                  "description": "Social media drafter.",
                  "required_fields": ["topic"],
                  "optional_fields": ["audience", "style"]
                }
              ]
            }
            """);

        result.Text.Should().BeNullOrEmpty();
        result.Cards.Should().ContainSingle(card => card.BlockId == "templates_list");
        var card = result.Cards.Single();
        card.Title.Should().Be("Available Templates");
        card.Text.Should().Contain("daily_report");
        card.Text.Should().Contain("social_media");
        card.Text.Should().NotContain("\"config\"");
        result.Actions.Should().Contain(a => a.ActionId == "open_daily_report_form");
        result.Actions.Should().Contain(a => a.ActionId == "open_social_media_form");
        result.Actions.Should().Contain(a => a.ActionId == "list_agents");
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
              "template": "daily_report",
              "status": "running",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "UTC",
              "last_run_at": "2026-04-25T05:30:00Z",
              "next_scheduled_run": "2026-04-26T09:00:00Z",
              "error_count": "0"
            }
            """);

        result.Text.Should().BeNullOrEmpty();
        result.Cards.Should().ContainSingle(card => card.BlockId == "agent_status:skill-runner-1");
        result.Cards.Single().Text.Should().Contain("Status: `running`");
        // Lifecycle buttons render as actions, not as embedded JSON in MessageContent.Text.
        result.Actions.Should().Contain(a => a.ActionId == "run_agent");
        result.Actions.Should().Contain(a => a.ActionId == "disable_agent");
        // `confirm_delete_agent` so the card-flow path keeps the explicit confirmation step.
        var deleteButton = result.Actions.Should().Contain(a => a.ActionId == "confirm_delete_agent").Subject;
        deleteButton.IsDanger.Should().BeTrue();
        deleteButton.Arguments.Should().Contain(new KeyValuePair<string, string>("agent_id", "skill-runner-1"));
        deleteButton.Arguments.Should().Contain(new KeyValuePair<string, string>("template", "daily_report"));
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
              "template": "daily_report",
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
    public void FormatToolResult_CreateSocialMedia_ReturnsStructuredCardNotJsonText()
    {
        var decision = AgentBuilderFlowDecision.ToolCall("create_social_media", """{"action":"create_agent"}""");
        var result = AgentBuilderCardFlow.FormatToolResult(
            decision,
            """
            {
              "status": "created",
              "agent_id": "skill-runner-sm-1",
              "workflow_id": "workflow-1",
              "next_scheduled_run": "2026-04-26T09:00:00Z"
            }
            """);

        result.Text.Should().BeNullOrEmpty();
        result.Cards.Should().ContainSingle(card => card.BlockId == "social_media_created:skill-runner-sm-1");
        result.Cards.Single().Text.Should().Contain("skill-runner-sm-1");
        result.Cards.Single().Text.Should().NotContain("\"config\"");
        result.Actions.Should().Contain(a => a.ActionId == "list_agents");
        result.Actions.Should().Contain(a => a.ActionId == "open_social_media_form");
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
        inbound.Extra["template"] = "daily_report";

        var decision = await AgentBuilderCardFlow.TryResolveAsync(inbound, userConfigQueryPort: null);

        decision.Should().NotBeNull();
        decision!.RequiresToolExecution.Should().BeFalse();
        decision.ReplyContent.Should().NotBeNull();
        decision.ReplyContent!.Text.Should().BeNullOrEmpty();
        decision.ReplyContent.Cards.Should().ContainSingle(card =>
            card.BlockId == "delete_confirm:skill-runner-1");
        decision.ReplyContent.Cards.Single().Text.Should().Contain("daily_report");
        var confirmButton = decision.ReplyContent.Actions.Should()
            .Contain(a => a.ActionId == "delete_agent").Subject;
        confirmButton.IsDanger.Should().BeTrue();
        confirmButton.Arguments.Should().Contain(new KeyValuePair<string, string>(
            "agent_id", "skill-runner-1"));
        decision.ReplyContent.Actions.Should().Contain(a => a.ActionId == "list_agents");
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
