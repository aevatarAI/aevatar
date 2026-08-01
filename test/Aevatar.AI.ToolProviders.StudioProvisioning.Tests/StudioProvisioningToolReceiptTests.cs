using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Auditing;
using Aevatar.AI.Core.Tools;
using Aevatar.Studio.Application.Provisioning;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.StudioProvisioning.Tests;

/// <summary>
/// Issue 3101 regression coverage. These tests drive the Studio provisioning tools
/// through the real <see cref="StreamingToolExecutor"/> so the provider receipt is
/// produced by <see cref="ToolCallReceiptFinalizer"/> exactly as it is in chat.
///
/// The reported production failure was a successful team mutation whose tool result
/// was replaced by the synthetic <c>tool_outcome_unknown</c> payload, which hid the
/// authoritative <c>team_id</c> from the next chat tool round and caused retries to
/// create additional Teams.
/// </summary>
public sealed class StudioProvisioningToolReceiptTests
{
    private const string CreateTeamToolName = "aevatar_create_team";

    [Fact]
    public async Task CreateTeam_ThroughExecutor_ShouldEmitTypedSuccessReceiptPreservingTeamIdentity()
    {
        var teamPort = new RecordingTeamProvisioningPort();
        using var _ = PushContext();

        var result = await ExecuteThroughExecutorAsync(
            teamPort,
            """{"display_name":"Personal Reminders","team_id":"team-alpha"}""");

        result.IsError.Should().BeFalse("a successful team mutation is a verified outcome");
        result.Result.Should().NotContain(ToolCallReceiptFinalizer.UnknownErrorMessage);

        var receipt = result.Receipt.Should().NotBeNull().And.Subject as AgentToolReceipt;
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.ErrorCode.Should().NotBe(ToolCallReceiptFinalizer.UnknownErrorCode);
        receipt.SideEffectKind.Should().Be("studio.team.create");
        receipt.SubjectKind.Should().Be("studio_team");
        receipt.SubjectId.Should().Be("team-alpha");

        // The authoritative identity must survive to the next chat tool round.
        using var document = JsonDocument.Parse(result.Result);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("team_id").GetString().Should().Be("team-alpha");
        root.GetProperty("scope_id").GetString().Should().Be("scope-current");
        root.GetProperty("team_url").GetString().Should().Be("/api/scopes/scope-current/teams/team-alpha");
    }

    [Fact]
    public async Task CreateTeam_ThroughExecutor_WhenMutationFails_ShouldEmitTypedErrorReceipt()
    {
        var teamPort = new RecordingTeamProvisioningPort();
        using var _ = PushContext(scopeId: null);

        var result = await ExecuteThroughExecutorAsync(
            teamPort,
            """{"display_name":"Personal Reminders"}""");

        result.IsError.Should().BeTrue();
        var receipt = result.Receipt.Should().NotBeNull().And.Subject as AgentToolReceipt;
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("caller_scope_unavailable");
        receipt.ErrorCode.Should().NotBe(ToolCallReceiptFinalizer.UnknownErrorCode);
        teamPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateTeam_RepeatedIntent_ShouldConvergeOnOneTeamIdentity()
    {
        var teamPort = new RecordingTeamProvisioningPort();
        const string arguments = """{"display_name":"Personal Reminders","description":"Weekly reminder team"}""";

        // Same logical create intent, replayed as three separate tool calls the way
        // chat retries after an ambiguous outcome.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var _ = PushContext(callId: $"call-{attempt}", requestId: $"request-{attempt}");
            var result = await ExecuteThroughExecutorAsync(teamPort, arguments);
            result.IsError.Should().BeFalse();
        }

        teamPort.Requests.Should().HaveCount(3);
        teamPort.Requests.Select(static request => request.TeamId)
            .Should().OnlyHaveUniqueItems().And.HaveCount(1,
                "one logical create intent must not mint a new Team identity per retry");
        teamPort.Requests.Should().AllSatisfy(static request =>
            request.TeamId.Should().NotBeNullOrWhiteSpace(
                "the tool must resolve a stable id instead of letting the service mint a random one"));
    }

    [Fact]
    public async Task CreateTeam_DistinctIntents_ShouldNotCollapseIntoOneTeamIdentity()
    {
        var teamPort = new RecordingTeamProvisioningPort();

        using (var _ = PushContext())
            await ExecuteThroughExecutorAsync(teamPort, """{"display_name":"Personal Reminders"}""");
        using (var _ = PushContext())
            await ExecuteThroughExecutorAsync(teamPort, """{"display_name":"Work Reminders"}""");

        teamPort.Requests.Select(static request => request.TeamId)
            .Should().OnlyHaveUniqueItems().And.HaveCount(2);
    }

    [Fact]
    public async Task CreateTeam_DerivedTeamId_ShouldSatisfyStudioTeamIdContract()
    {
        var teamPort = new RecordingTeamProvisioningPort();
        using var _ = PushContext();

        await ExecuteThroughExecutorAsync(teamPort, """{"display_name":"Personal Reminders"}""");

        var teamId = teamPort.Requests.Should().ContainSingle().Which.TeamId;
        teamId.Should().MatchRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$");
    }

    private static async Task<ToolExecutionResult> ExecuteThroughExecutorAsync(
        IStudioTeamProvisioningPort teamPort,
        string argumentsJson)
    {
        var source = new CreateStudioTeamToolSource(teamPort);
        var tools = new ToolManager();
        tools.Register(await source.DiscoverToolsAsync());

        var executor = new StreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();
        executor.AddTool(executionState, new ToolCall
        {
            Id = "tc-1",
            Name = CreateTeamToolName,
            ArgumentsJson = argumentsJson,
        });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        return results.Should().ContainSingle().Which;
    }

    private static AgentToolContextScope PushContext(
        string? scopeId = "scope-current",
        string? requestId = "request-1",
        string? callId = "call-1") =>
        AgentToolContextScope.Push(new AgentToolExecutionContext(
            new AgentToolRequestIdentity(requestId, callId, null),
            new AgentToolCredentials("access-token-1", "org-token", "sender-token"),
            new AgentToolCallerContext(scopeId, "owner-1", "response-1", null),
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)));

    private sealed class RecordingTeamProvisioningPort : IStudioTeamProvisioningPort
    {
        public List<StudioTeamProvisioningRequest> Requests { get; } = [];

        public Task<StudioTeamProvisioningResult> CreateAsync(
            StudioTeamProvisioningRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new StudioTeamProvisioningResult(
                Success: true,
                ScopeId: request.ScopeId,
                TeamId: request.TeamId ?? "team-generated",
                DisplayName: request.DisplayName,
                Description: request.Description ?? string.Empty,
                LifecycleStage: "active",
                MemberCount: 0,
                CreatedAt: DateTimeOffset.Parse("2026-07-31T00:00:00Z"),
                UpdatedAt: DateTimeOffset.Parse("2026-07-31T00:00:00Z")));
        }
    }
}
