using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.StudioProvisioning.Tests;

public sealed class ProvisionWorkflowScheduleToolTests
{
    private const string ScheduleToolName = "aevatar_provision_workflow_schedule";
    private const string CreateTeamToolName = "aevatar_create_team";

    [Fact]
    public async Task ToolSource_ShouldDiscoverProvisionWorkflowScheduleTool()
    {
        var port = new RecordingProvisioningPort();
        var source = new ProvisionWorkflowScheduleToolSource(port);

        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be(ScheduleToolName);
    }

    [Fact]
    public async Task ToolSource_WhenTeamPortRegistered_ShouldDiscoverCreateTeamTool()
    {
        var source = new CreateStudioTeamToolSource(new RecordingTeamProvisioningPort());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be(CreateTeamToolName);
    }

    [Fact]
    public async Task AddStudioProvisioningTools_WhenTeamPortRegistered_ShouldExposeCreateTeamTool()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowScheduleProvisioningPort, RecordingProvisioningPort>();
        services.AddSingleton<IStudioTeamProvisioningPort, RecordingTeamProvisioningPort>();
        services.AddStudioProvisioningTools();

        var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IAgentToolSource>();
        var toolNames = new List<string>();
        foreach (var source in sources)
        {
            var tools = await source.DiscoverToolsAsync();
            toolNames.AddRange(tools.Select(static tool => tool.Name));
        }

        toolNames.Should().Contain(ScheduleToolName);
        toolNames.Should().Contain(CreateTeamToolName);
    }

    [Fact]
    public async Task ToolSource_WhenTeamPortMissing_ShouldNotDiscoverCreateTeamTool()
    {
        var source = new CreateStudioTeamToolSource();

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateTeam_ShouldCallTeamPortWithCallerScope()
    {
        var teamPort = new RecordingTeamProvisioningPort();
        var tool = await DiscoverCreateTeamToolAsync(teamPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "display_name": "Alpha Team",
              "description": "Current caller scope team",
              "team_id": "team-alpha"
            }
            """);

        teamPort.LastRequest.Should().NotBeNull();
        teamPort.LastRequest!.ScopeId.Should().Be("scope-current");
        teamPort.LastRequest.DisplayName.Should().Be("Alpha Team");
        teamPort.LastRequest.Description.Should().Be("Current caller scope team");
        teamPort.LastRequest.TeamId.Should().Be("team-alpha");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("scope_id").GetString().Should().Be("scope-current");
        root.GetProperty("team_id").GetString().Should().Be("team-alpha");
        root.GetProperty("team_url").GetString().Should().Be("/api/scopes/scope-current/teams/team-alpha");
    }

    [Fact]
    public async Task CreateTeam_WhenScopeMissing_ShouldReturnStructuredErrorAndNotCallPort()
    {
        var teamPort = new RecordingTeamProvisioningPort();
        var tool = await DiscoverCreateTeamToolAsync(teamPort);

        using var _ = PushContext(scopeId: null, ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"display_name":"Alpha Team"}""");

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        teamPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task CreateTeam_WhenModelSuppliesScope_ShouldRejectUnknownArgumentAndNotCallPort()
    {
        var teamPort = new RecordingTeamProvisioningPort();
        var tool = await DiscoverCreateTeamToolAsync(teamPort);

        using var _ = PushContext(scopeId: "scope-context", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "scope_id": "scope-model",
              "display_name": "Alpha Team",
              "team_id": "team-alpha"
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("Unknown argument: scope_id");
        teamPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task CreateTeam_ShouldUseSharedCreateScopedResourceApprovalPolicy()
    {
        var tool = await DiscoverCreateTeamToolAsync(new RecordingTeamProvisioningPort());

        tool.ApprovalMode.Should().Be(ToolApprovalPolicies.CreateScopedResource);
        tool.Should().NotBeAssignableTo<IAgentToolCapabilityDescriptor>();
    }

    [Fact]
    public async Task ScheduleTool_ShouldDeclareDirectChannelChatExclusion()
    {
        var tool = await DiscoverToolAsync(new RecordingProvisioningPort());

        var descriptor = tool.Should().BeAssignableTo<IAgentToolCapabilityDescriptor>().Subject;
        descriptor.Capabilities.Should().Contain(AgentToolCapabilities.ExcludeFromDirectChannelChat);
    }

    [Fact]
    public async Task Execute_ShouldMapArgumentsAndContextOntoProvisioningRequest()
    {
        var port = new RecordingProvisioningPort(new WorkflowScheduleProvisioningResult(
            MemberId: "member-1",
            ScopeId: "scope-1",
            BindingStatus: "accepted",
            ObservatoryUrl: "/workflow/observatory")
        {
            ScheduleId = "schedule-1",
            BindingRunId = "bind-run-1",
        });
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(scopeId: "scope-1", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_yaml": "name: daily-tech-news\nroles: []\n",
              "display_name": "Daily Tech News",
              "prompt": "summarize today's tech news",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai",
              "run_immediately": false
            }
            """);

        port.LastRequest.Should().NotBeNull();
        var request = port.LastRequest!;
        request.ScopeId.Should().Be("scope-1");
        request.DisplayName.Should().Be("Daily Tech News");
        request.WorkflowYaml.Should().Contain("name: daily-tech-news");
        request.Prompt.Should().Be("summarize today's tech news");
        request.ScheduleCron.Should().Be("0 9 * * *");
        request.ScheduleTimezone.Should().Be("Asia/Shanghai");
        request.RunImmediately.Should().BeFalse();
        // Caller identity is taken from the tool execution context (W1-threaded), not arguments.
        request.CallerSubjectExternalUserId.Should().Be("owner-1");

        // Result surfaces the schedule + Observatory link.
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("accepted");
        root.GetProperty("member_id").GetString().Should().Be("member-1");
        root.GetProperty("schedule_id").GetString().Should().Be("schedule-1");
        root.GetProperty("observatory_url").GetString().Should().Be("/workflow/observatory");
    }

    [Fact]
    public async Task Execute_WhenRunImmediatelyAbsent_ShouldDefaultToTrue()
    {
        var port = new RecordingProvisioningPort();
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(scopeId: "scope-1", ownerSubject: "owner-1", accessToken: "access-token-1");
        await tool.ExecuteAsync("""
            {
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        port.LastRequest.Should().NotBeNull();
        port.LastRequest!.RunImmediately.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_WhenScopeMissing_ShouldReturnCallerScopeUnavailableAndNotCallPort()
    {
        var port = new RecordingProvisioningPort();
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(scopeId: null, ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        port.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Execute_WhenWorkflowYamlMissing_ShouldReturnInvalidArguments()
    {
        var port = new RecordingProvisioningPort();
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(scopeId: "scope-1", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "display_name": "Demo"
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        port.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Execute_WhenProvisioningThrowsValidationError_ShouldReturnTypedInvalidArguments()
    {
        var port = new RecordingProvisioningPort
        {
            Throw = new InvalidOperationException("WorkflowYaml is required."),
        };
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(scopeId: "scope-1", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
    }

    [Fact]
    public async Task Execute_Result_ShouldNotCarryAnyLarkOrChannelFields()
    {
        var port = new RecordingProvisioningPort(new WorkflowScheduleProvisioningResult(
            MemberId: "member-1",
            ScopeId: "scope-1",
            BindingStatus: "accepted",
            ObservatoryUrl: "/workflow/observatory")
        {
            ScheduleId = "schedule-1",
        });
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(scopeId: "scope-1", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        var lower = output.ToLowerInvariant();
        lower.Should().NotContain("lark");
        lower.Should().NotContain("feishu");
        lower.Should().NotContain("channel");
        lower.Should().NotContain("conversation");
        lower.Should().NotContain("receive_id");
        lower.Should().NotContain("bot");
    }

    private static async Task<IAgentTool> DiscoverToolAsync(IWorkflowScheduleProvisioningPort port)
    {
        var source = new ProvisionWorkflowScheduleToolSource(port);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == ScheduleToolName);
    }

    private static async Task<IAgentTool> DiscoverCreateTeamToolAsync(IStudioTeamProvisioningPort teamPort)
    {
        var source = new CreateStudioTeamToolSource(teamPort);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == CreateTeamToolName);
    }

    private static AgentToolContextScope PushContext(string? scopeId, string? ownerSubject, string? accessToken)
    {
        return AgentToolContextScope.Push(new AgentToolExecutionContext(
            new AgentToolRequestIdentity("request-1", "call-1"),
            new AgentToolCredentials(accessToken, "org-token", "sender-token"),
            new AgentToolCallerContext(scopeId, ownerSubject, "response-1"),
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    private static string? ErrorCode(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.TryGetProperty("error", out var error)
            && error.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    private static string? ErrorMessage(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.TryGetProperty("error", out var error)
            && error.TryGetProperty("message", out var message)
            ? message.GetString()
            : null;
    }

    private sealed class RecordingProvisioningPort : IWorkflowScheduleProvisioningPort
    {
        private readonly WorkflowScheduleProvisioningResult _result;

        public RecordingProvisioningPort(WorkflowScheduleProvisioningResult? result = null)
        {
            _result = result ?? new WorkflowScheduleProvisioningResult(
                MemberId: "member-default",
                ScopeId: "scope-default",
                BindingStatus: "accepted",
                ObservatoryUrl: "/workflow/observatory");
        }

        public WorkflowScheduleProvisioningRequest? LastRequest { get; private set; }

        public Exception? Throw { get; init; }

        public Task<WorkflowScheduleProvisioningResult> ProvisionAsync(
            WorkflowScheduleProvisioningRequest request,
            CancellationToken ct = default)
        {
            if (Throw != null)
                throw Throw;

            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingTeamProvisioningPort : IStudioTeamProvisioningPort
    {
        public StudioTeamProvisioningRequest? LastRequest { get; private set; }

        public Task<StudioTeamProvisioningResult> CreateAsync(
            StudioTeamProvisioningRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new StudioTeamProvisioningResult(
                Success: true,
                ScopeId: request.ScopeId,
                TeamId: request.TeamId ?? "team-generated",
                DisplayName: request.DisplayName,
                Description: request.Description ?? string.Empty,
                LifecycleStage: "active",
                MemberCount: 0,
                CreatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                UpdatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z")));
        }
    }
}
