using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aevatar.AI.ToolProviders.StudioProvisioning.Tests;

public sealed class ProvisionWorkflowScheduleToolTests
{
    private const string ScheduleToolName = "aevatar_provision_workflow_schedule";
    private const string CreateTeamToolName = "aevatar_create_team";
    private const string ListTeamsToolName = "aevatar_list_teams";
    private const string GetTeamToolName = "aevatar_get_team";
    private const string CreateMemberToolName = "aevatar_create_member";
    private const string CreateMemberWorkflowDraftToolName = "aevatar_create_member_workflow_draft";
    private const string ListMembersToolName = "aevatar_list_members";
    private const string GetMemberToolName = "aevatar_get_member";
    private const string ListWorkflowsToolName = "aevatar_list_workflows";
    private const string ListSchedulesToolName = "aevatar_list_schedules";
    private const string GetScheduleToolName = "aevatar_get_schedule";
    private const string BindMemberWorkflowToolName = "aevatar_bind_member_workflow";
    private const string ScheduleMemberWorkflowToolName = "aevatar_schedule_member_workflow";

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
    public async Task ToolSource_WhenMemberPortRegistered_ShouldDiscoverCreateMemberTool()
    {
        var source = new CreateStudioMemberToolSource(new RecordingMemberProvisioningPort());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be(CreateMemberToolName);
    }

    [Fact]
    public async Task AddStudioProvisioningTools_WhenPortsRegistered_ShouldExposeScheduleTeamMemberAndBindingTools()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowScheduleProvisioningPort, RecordingProvisioningPort>();
        services.AddSingleton<IStudioTeamProvisioningPort, RecordingTeamProvisioningPort>();
        services.AddSingleton<IStudioTeamQueryPort, RecordingTeamQueryPort>();
        services.AddSingleton<IStudioMemberProvisioningPort, RecordingMemberProvisioningPort>();
        services.AddSingleton<IStudioMemberWorkflowDraftProvisioningPort, RecordingMemberWorkflowDraftProvisioningPort>();
        services.AddSingleton<IStudioMemberQueryPort, RecordingMemberQueryPort>();
        services.AddSingleton<IStudioMemberAutomationQueryPort, RecordingMemberAutomationQueryPort>();
        services.AddSingleton<IStudioMemberWorkflowBindingPort, RecordingMemberWorkflowBindingPort>();
        services.AddSingleton<IStudioMemberWorkflowSchedulePort, RecordingMemberWorkflowSchedulePort>();
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
        toolNames.Should().Contain(ListTeamsToolName);
        toolNames.Should().Contain(GetTeamToolName);
        toolNames.Should().Contain(CreateMemberToolName);
        toolNames.Should().Contain(CreateMemberWorkflowDraftToolName);
        toolNames.Should().Contain(ListMembersToolName);
        toolNames.Should().Contain(GetMemberToolName);
        toolNames.Should().Contain(ListWorkflowsToolName);
        toolNames.Should().Contain(ListSchedulesToolName);
        toolNames.Should().Contain(GetScheduleToolName);
        toolNames.Should().Contain(BindMemberWorkflowToolName);
        toolNames.Should().Contain(ScheduleMemberWorkflowToolName);
    }

    [Fact]
    public async Task ToolSource_WhenTeamPortMissing_ShouldNotDiscoverCreateTeamTool()
    {
        var source = new CreateStudioTeamToolSource();

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolSource_WhenTeamQueryPortRegistered_ShouldDiscoverReadOnlyTeamQueryTools()
    {
        var source = new StudioTeamQueryToolSource(new RecordingTeamQueryPort());

        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(ListTeamsToolName, GetTeamToolName);
        tools.Should().OnlyContain(static tool => tool.IsReadOnly);
        tools.Should().OnlyContain(static tool => !tool.IsDestructive);
    }

    [Fact]
    public async Task ToolSource_WhenTeamQueryPortMissing_ShouldNotDiscoverTeamQueryTools()
    {
        var source = new StudioTeamQueryToolSource();

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolSource_WhenMemberPortMissing_ShouldNotDiscoverCreateMemberTool()
    {
        var source = new CreateStudioMemberToolSource();

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolSource_WhenMemberQueryPortRegistered_ShouldDiscoverReadOnlyMemberQueryTools()
    {
        var source = new StudioMemberQueryToolSource(new RecordingMemberQueryPort());

        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(ListMembersToolName, GetMemberToolName);
        tools.Should().OnlyContain(static tool => tool.IsReadOnly);
        tools.Should().OnlyContain(static tool => !tool.IsDestructive);
    }

    [Fact]
    public async Task ToolSource_WhenMemberQueryPortMissing_ShouldNotDiscoverMemberQueryTools()
    {
        var source = new StudioMemberQueryToolSource();

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolSource_WhenScheduleQueryPortRegistered_ShouldDiscoverReadOnlyScheduleQueryTools()
    {
        var source = new StudioScheduleQueryToolSource(new RecordingMemberAutomationQueryPort());

        var tools = await source.DiscoverToolsAsync();

        tools.Select(static tool => tool.Name).Should().BeEquivalentTo(ListSchedulesToolName, GetScheduleToolName);
        tools.Should().OnlyContain(static tool => tool.IsReadOnly);
        tools.Should().OnlyContain(static tool => !tool.IsDestructive);
    }

    [Fact]
    public async Task ToolSource_WhenScheduleQueryServiceMissing_ShouldNotDiscoverScheduleQueryTools()
    {
        var source = new StudioScheduleQueryToolSource();

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolSource_WhenBindingPortRegistered_ShouldDiscoverBindMemberWorkflowTool()
    {
        var source = new BindStudioMemberWorkflowToolSource(new RecordingMemberWorkflowBindingPort());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be(BindMemberWorkflowToolName);
    }

    [Fact]
    public async Task ToolSource_WhenBindingPortMissing_ShouldNotDiscoverBindMemberWorkflowTool()
    {
        var source = new BindStudioMemberWorkflowToolSource();

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
    public async Task CreateTeam_WhenOwnerScopePresent_ShouldCallPortWithOwnerScope()
    {
        var teamPort = new RecordingTeamProvisioningPort();
        var tool = await DiscoverCreateTeamToolAsync(teamPort);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            ownerScopeId: "owner-scope");
        var output = await tool.ExecuteAsync("""
            {
              "display_name": "Alpha Team",
              "team_id": "team-alpha"
            }
            """);

        teamPort.LastRequest.Should().NotBeNull();
        teamPort.LastRequest!.ScopeId.Should().Be("owner-scope");

        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("scope_id").GetString().Should().Be("owner-scope");
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
    public async Task CreateMember_ShouldCallMemberPortWithCallerScope()
    {
        var memberPort = new RecordingMemberProvisioningPort();
        var tool = await DiscoverCreateMemberToolAsync(memberPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "display_name": "Alpha Member",
              "implementation_kind": "workflow",
              "description": "Current caller scope member",
              "member_id": "member-alpha",
              "team_id": "team-alpha"
            }
            """);

        memberPort.LastRequest.Should().NotBeNull();
        memberPort.LastRequest!.ScopeId.Should().Be("scope-current");
        memberPort.LastRequest.DisplayName.Should().Be("Alpha Member");
        memberPort.LastRequest.ImplementationKind.Should().Be("workflow");
        memberPort.LastRequest.Description.Should().Be("Current caller scope member");
        memberPort.LastRequest.MemberId.Should().Be("member-alpha");
        memberPort.LastRequest.TeamId.Should().Be("team-alpha");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("scope_id").GetString().Should().Be("scope-current");
        root.GetProperty("member_id").GetString().Should().Be("member-alpha");
        root.GetProperty("team_id").GetString().Should().Be("team-alpha");
        root.GetProperty("member_url").GetString().Should().Be("/api/scopes/scope-current/members/member-alpha");
    }

    [Fact]
    public async Task CreateMember_WhenOwnerScopePresent_ShouldCallPortWithOwnerScope()
    {
        var memberPort = new RecordingMemberProvisioningPort();
        var tool = await DiscoverCreateMemberToolAsync(memberPort);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            ownerScopeId: "owner-scope");
        var output = await tool.ExecuteAsync("""
            {
              "display_name": "Alpha Member",
              "implementation_kind": "workflow",
              "member_id": "member-alpha",
              "team_id": "team-alpha"
            }
            """);

        memberPort.LastRequest.Should().NotBeNull();
        memberPort.LastRequest!.ScopeId.Should().Be("owner-scope");
        memberPort.LastRequest.TeamId.Should().Be("team-alpha");

        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("scope_id").GetString().Should().Be("owner-scope");
    }

    [Fact]
    public async Task CreateMember_WhenWorkflowTeamIdMissing_ShouldReturnInvalidArgumentsAndNotCallPort()
    {
        var memberPort = new RecordingMemberProvisioningPort();
        var tool = await DiscoverCreateMemberToolAsync(memberPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "display_name": "Alpha Member",
              "implementation_kind": " Workflow "
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("team_id is required for workflow members.");
        memberPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task CreateMember_WhenScopeMissing_ShouldReturnStructuredErrorAndNotCallPort()
    {
        var memberPort = new RecordingMemberProvisioningPort();
        var tool = await DiscoverCreateMemberToolAsync(memberPort);

        using var _ = PushContext(scopeId: null, ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"display_name":"Alpha Member","implementation_kind":"workflow"}""");

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        memberPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task CreateMember_WhenModelSuppliesScope_ShouldRejectUnknownArgumentAndNotCallPort()
    {
        var memberPort = new RecordingMemberProvisioningPort();
        var tool = await DiscoverCreateMemberToolAsync(memberPort);

        using var _ = PushContext(scopeId: "scope-context", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "scope_id": "scope-model",
              "display_name": "Alpha Member",
              "implementation_kind": "workflow",
              "member_id": "member-alpha"
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("Unknown argument: scope_id");
        memberPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task CreateMember_ShouldUseSharedCreateScopedResourceApprovalPolicy()
    {
        var tool = await DiscoverCreateMemberToolAsync(new RecordingMemberProvisioningPort());

        tool.ApprovalMode.Should().Be(ToolApprovalPolicies.CreateScopedResource);
        tool.Should().NotBeAssignableTo<IAgentToolCapabilityDescriptor>();
    }

    [Fact]
    public async Task CreateTeam_ThroughStreamingExecutor_ShouldKeepVerifiedMutationSuccess()
    {
        var teamPort = new RecordingTeamProvisioningPort();
        var tool = await DiscoverCreateTeamToolAsync(teamPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var toolResult = await ExecuteToolThroughExecutorAsync(
            tool,
            "tc-create-team",
            CreateTeamToolName,
            """{"display_name":"Alpha Team","team_id":"team-alpha"}""");

        toolResult.IsError.Should().BeFalse();
        toolResult.Result.Should().Contain("\"team_id\":\"team-alpha\"");
        toolResult.Result.Should().NotBe("""{"status":"unknown","message":"The tool outcome could not be verified."}""");
        var receipt = toolResult.Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.NeverRequire);
        receipt.SideEffectKind.Should().Be("studio.team.create");
        receipt.SubjectKind.Should().Be("studio_team");
        receipt.SubjectId.Should().Be("team-alpha");
        receipt.ResultJson.Should().Be(toolResult.Result);
        teamPort.LastRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateTeam_ThroughStreamingExecutor_ShouldKeepStructuredMutationError()
    {
        var teamPort = new RecordingTeamProvisioningPort();
        var tool = await DiscoverCreateTeamToolAsync(teamPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var toolResult = await ExecuteToolThroughExecutorAsync(
            tool,
            "tc-create-team-error",
            CreateTeamToolName,
            """{"scope_id":"scope-model","display_name":"Alpha Team"}""");

        toolResult.IsError.Should().BeTrue();
        toolResult.Result.Should().Contain("invalid_arguments");
        toolResult.Result.Should().NotBe("""{"status":"unknown","message":"The tool outcome could not be verified."}""");
        var receipt = toolResult.Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("invalid_arguments");
        receipt.SideEffectKind.Should().Be("studio.team.create");
        receipt.ResultJson.Should().Be(toolResult.Result);
        teamPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task CreateTeam_CreateResultReceipt_WithPartialMutationJson_ShouldReturnNull()
    {
        var tool = await DiscoverCreateTeamToolAsync(new RecordingTeamProvisioningPort());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            CreateTeamToolName,
            "{}",
            """{"team_id":"team-alpha"}""");

        receipt.Should().BeNull();
    }

    [Fact]
    public async Task CreateTeam_CreateResultReceipt_WithSuccessButPartialMutationJson_ShouldReturnNull()
    {
        var tool = await DiscoverCreateTeamToolAsync(new RecordingTeamProvisioningPort());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            CreateTeamToolName,
            "{}",
            """{"success":true,"team_id":"team-alpha"}""");

        receipt.Should().BeNull();
    }

    [Fact]
    public async Task CreateTeam_CreateResultReceipt_WithSuccessButWrongMutationJsonKind_ShouldReturnNull()
    {
        var tool = await DiscoverCreateTeamToolAsync(new RecordingTeamProvisioningPort());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            CreateTeamToolName,
            "{}",
            """{"success":true,"team_id":"team-alpha","scope_id":123,"display_name":"Alpha Team","lifecycle_stage":"active","team_url":"/teams/team-alpha"}""");

        receipt.Should().BeNull();
    }

    [Fact]
    public async Task CreateTeam_CreateResultReceipt_WithMalformedMutationJson_ShouldReturnNull()
    {
        var tool = await DiscoverCreateTeamToolAsync(new RecordingTeamProvisioningPort());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            CreateTeamToolName,
            "{}",
            "{");

        receipt.Should().BeNull();
    }

    [Fact]
    public async Task CreateMember_ThroughStreamingExecutor_ShouldKeepVerifiedMutationSuccess()
    {
        var memberPort = new RecordingMemberProvisioningPort();
        var tool = await DiscoverCreateMemberToolAsync(memberPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var toolResult = await ExecuteToolThroughExecutorAsync(
            tool,
            "tc-create-member",
            CreateMemberToolName,
            """{"display_name":"Alpha Member","implementation_kind":"workflow","member_id":"member-alpha","team_id":"team-alpha"}""");

        toolResult.IsError.Should().BeFalse();
        toolResult.Result.Should().Contain("\"member_id\":\"member-alpha\"");
        toolResult.Result.Should().NotBe("""{"status":"unknown","message":"The tool outcome could not be verified."}""");
        var receipt = toolResult.Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.SideEffectKind.Should().Be("studio.member.create");
        receipt.SubjectKind.Should().Be("studio_member");
        receipt.SubjectId.Should().Be("member-alpha");
        receipt.ResultJson.Should().Be(toolResult.Result);
    }

    [Fact]
    public async Task CreateMemberWorkflowDraft_CreateResultReceipt_WithAcceptedDraftAck_ShouldReturnNull()
    {
        var tool = await DiscoverCreateMemberWorkflowDraftToolAsync(new RecordingMemberWorkflowDraftProvisioningPort());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            CreateMemberWorkflowDraftToolName,
            "{}",
            """{"status":"draft_save_accepted","runnable":false,"binding_status":"not_bound","scope_id":"scope-current","team_id":"team-alpha","member_id":"member-alpha","workflow_id":"workflow-alpha","studio_url":"/studio/workflows/workflow-alpha","command_id":"command-alpha","ack_stage":"dispatch_accepted","actor_id":"actor-alpha","workspace_id":"workspace-alpha","acked_at_utc":"2026-07-01T00:00:00Z","readiness":{"readable":true,"stage":"draft_saved","message":"Draft saved."},"blockers":[]}""");

        receipt.Should().BeNull();
    }

    [Fact]
    public async Task CreateMemberWorkflowDraft_CreateResultReceipt_WithStructuredError_ShouldReturnErrorReceipt()
    {
        var tool = await DiscoverCreateMemberWorkflowDraftToolAsync(new RecordingMemberWorkflowDraftProvisioningPort());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            CreateMemberWorkflowDraftToolName,
            "{}",
            """{"error":{"code":"invalid_arguments","message":"workflow_yaml is required."}}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.SideEffectKind.Should().Be("studio.workflow_draft.create");
        receipt.ErrorCode.Should().Be("invalid_arguments");
        receipt.ResultJson.Should().Contain("workflow_yaml is required");
    }

    [Fact]
    public async Task CreateMemberWorkflowDraft_CreateResultReceipt_WithSuccessButNoDraftStatus_ShouldReturnNull()
    {
        var tool = await DiscoverCreateMemberWorkflowDraftToolAsync(new RecordingMemberWorkflowDraftProvisioningPort());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            CreateMemberWorkflowDraftToolName,
            "{}",
            """{"success":true,"workflow_id":"workflow-alpha"}""");

        receipt.Should().BeNull();
    }

    [Fact]
    public async Task BindMemberWorkflow_ThroughStreamingExecutor_ShouldKeepVerifiedMutationSuccess()
    {
        var bindingPort = new RecordingMemberWorkflowBindingPort();
        var tool = await DiscoverBindMemberWorkflowToolAsync(bindingPort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var toolResult = await ExecuteToolThroughExecutorAsync(
            tool,
            "tc-bind-member-workflow",
            BindMemberWorkflowToolName,
            """{"member_id":"member-alpha","workflow_yaml":"name: demo\n","workflow_id":"workflow-alpha"}""");

        toolResult.IsError.Should().BeFalse();
        toolResult.Result.Should().Contain("\"member_id\":\"member-alpha\"");
        toolResult.Result.Should().NotBe("""{"status":"unknown","message":"The tool outcome could not be verified."}""");
        var receipt = toolResult.Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.SideEffectKind.Should().Be("studio.member.workflow.bind");
        receipt.SubjectKind.Should().Be("studio_member_workflow_binding");
        receipt.SubjectId.Should().Be("member-alpha");
        receipt.ResultJson.Should().Be(toolResult.Result);
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_ThroughStreamingExecutor_ShouldKeepVerifiedMutationSuccess()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var toolResult = await ExecuteToolThroughExecutorAsync(
            tool,
            "tc-schedule-member-workflow",
            ScheduleMemberWorkflowToolName,
            """{"member_id":"member-alpha","schedule_cron":"0 9 * * *","schedule_timezone":"Asia/Shanghai"}""");

        toolResult.IsError.Should().BeFalse();
        toolResult.Result.Should().Contain("\"schedule_id\":\"schedule-member-1\"");
        toolResult.Result.Should().NotBe("""{"status":"unknown","message":"The tool outcome could not be verified."}""");
        var receipt = toolResult.Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.SideEffectKind.Should().Be("studio.member.workflow.schedule");
        receipt.SubjectKind.Should().Be("studio_member_workflow_schedule");
        receipt.SubjectId.Should().Be("schedule-member-1");
        receipt.ResultJson.Should().Be(toolResult.Result);
    }

    [Fact]
    public async Task ListTeams_ShouldCallReadPortWithCallerScopeAndPage()
    {
        var teamQueryPort = new RecordingTeamQueryPort();
        var tool = await DiscoverListTeamsToolAsync(teamQueryPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "page_size": 25,
              "page_token": "page-2"
            }
            """);

        teamQueryPort.LastListScopeId.Should().Be("scope-current");
        teamQueryPort.LastListPage.Should().Be(new StudioTeamRosterPageRequest(25, "page-2"));

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("scope_id").GetString().Should().Be("scope-current");
        root.GetProperty("teams")[0].GetProperty("team_id").GetString().Should().Be("team-alpha");
        root.GetProperty("teams")[0].GetProperty("team_url").GetString()
            .Should().Be("/api/scopes/scope-current/teams/team-alpha");
        root.GetProperty("next_page_token").GetString().Should().Be("next-page");
    }

    [Fact]
    public async Task ListTeams_ThroughStreamingExecutor_ShouldKeepVerifiedReadOnlySuccess()
    {
        var teamQueryPort = new RecordingTeamQueryPort();
        var tool = await DiscoverListTeamsToolAsync(teamQueryPort);
        var tools = new ToolManager();
        tools.Register(tool);
        var executor = new StreamingToolExecutor(
            tools,
            toolExecutionPort: CreateToolExecutionPort());
        using var executionState = executor.CreateExecutionState();

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var prepared = await executor.PrepareBatchAsync(
            "studio-provisioning-test:tc-list-teams",
            round: 0,
            [new ToolCall
            {
                Id = "tc-list-teams",
                Name = ListTeamsToolName,
                ArgumentsJson = "{}",
            }]);
        executor.AddTool(executionState, prepared.Single());

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        var toolResult = results.Should().ContainSingle().Which;
        toolResult.IsError.Should().BeFalse();
        toolResult.Result.Should().Contain("\"teams\"");
        toolResult.Result.Should().NotBe("""{"status":"unknown","message":"The tool outcome could not be verified."}""");
        var receipt = toolResult.Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.NeverRequire);
        receipt.IsDestructive.Should().BeFalse();
        receipt.SideEffectKind.Should().BeEmpty();
        receipt.ResultJson.Should().Be(toolResult.Result);
    }

    [Fact]
    public async Task ListTeams_CreateResultReceipt_WithPartialReadOnlyJson_ShouldReturnNull()
    {
        var tool = await DiscoverListTeamsToolAsync(new RecordingTeamQueryPort());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            ListTeamsToolName,
            "{}",
            """{"scope_id":"scope-current"}""");

        receipt.Should().BeNull();
    }

    [Fact]
    public async Task ListTeams_CreateResultReceipt_WithWrongReadOnlyJsonKind_ShouldReturnNull()
    {
        var tool = await DiscoverListTeamsToolAsync(new RecordingTeamQueryPort());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            ListTeamsToolName,
            "{}",
            """{"scope_id":"scope-current","teams":null}""");

        receipt.Should().BeNull();
    }

    [Fact]
    public async Task ListTeams_CreateResultReceipt_WithMalformedReadOnlyJson_ShouldReturnNull()
    {
        var tool = await DiscoverListTeamsToolAsync(new RecordingTeamQueryPort());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            ListTeamsToolName,
            "{}",
            "{");

        receipt.Should().BeNull();
    }

    [Fact]
    public async Task ListTeams_ThroughStreamingExecutor_ShouldKeepStructuredReadOnlyError()
    {
        var teamQueryPort = new RecordingTeamQueryPort();
        var tool = await DiscoverListTeamsToolAsync(teamQueryPort);
        var tools = new ToolManager();
        tools.Register(tool);
        var executor = new StreamingToolExecutor(
            tools,
            toolExecutionPort: CreateToolExecutionPort());
        using var executionState = executor.CreateExecutionState();

        using var _ = PushContext(scopeId: "scope-context", ownerSubject: "owner-1", accessToken: "access-token-1");
        var prepared = await executor.PrepareBatchAsync(
            "studio-provisioning-test:tc-list-teams-error",
            round: 0,
            [new ToolCall
            {
                Id = "tc-list-teams-error",
                Name = ListTeamsToolName,
                ArgumentsJson = """{"scope_id":"scope-model"}""",
            }]);
        executor.AddTool(executionState, prepared.Single());

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        var toolResult = results.Should().ContainSingle().Which;
        toolResult.IsError.Should().BeTrue();
        toolResult.Result.Should().Contain("invalid_arguments");
        toolResult.Result.Should().NotBe("""{"status":"unknown","message":"The tool outcome could not be verified."}""");
        var receipt = toolResult.Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("invalid_arguments");
        receipt.ResultJson.Should().Be(toolResult.Result);
        teamQueryPort.ListCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ListWorkflows_ThroughStreamingExecutor_ShouldKeepVerifiedReadOnlySuccess()
    {
        var memberQueryPort = new RecordingMemberQueryPort();
        var tool = await DiscoverListWorkflowsToolAsync(memberQueryPort);
        var tools = new ToolManager();
        tools.Register(tool);
        var executor = new StreamingToolExecutor(
            tools,
            toolExecutionPort: CreateToolExecutionPort());
        using var executionState = executor.CreateExecutionState();

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var prepared = await executor.PrepareBatchAsync(
            "studio-provisioning-test:tc-list-workflows",
            round: 0,
            [new ToolCall
            {
                Id = "tc-list-workflows",
                Name = ListWorkflowsToolName,
                ArgumentsJson = "{}",
            }]);
        executor.AddTool(executionState, prepared.Single());

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        var toolResult = results.Should().ContainSingle().Which;
        toolResult.IsError.Should().BeFalse();
        toolResult.Result.Should().Contain("\"workflows\"");
        toolResult.Result.Should().NotBe("""{"status":"unknown","message":"The tool outcome could not be verified."}""");
        var receipt = toolResult.Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.ResultJson.Should().Be(toolResult.Result);
        memberQueryPort.LastListScopeId.Should().Be("scope-current");
    }

    [Fact]
    public async Task ListTeams_WhenOwnerScopePresent_ShouldCallReadPortWithOwnerScope()
    {
        var teamQueryPort = new RecordingTeamQueryPort();
        var tool = await DiscoverListTeamsToolAsync(teamQueryPort);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            ownerScopeId: "owner-scope");
        var output = await tool.ExecuteAsync("{}");

        teamQueryPort.LastListScopeId.Should().Be("owner-scope");

        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("scope_id").GetString().Should().Be("owner-scope");
    }

    [Fact]
    public async Task GetTeam_ShouldCallReadPortWithCallerScopeAndTeamId()
    {
        var teamQueryPort = new RecordingTeamQueryPort();
        var tool = await DiscoverGetTeamToolAsync(teamQueryPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"team_id":"team-alpha"}""");

        teamQueryPort.LastGetScopeId.Should().Be("scope-current");
        teamQueryPort.LastGetTeamId.Should().Be("team-alpha");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("scope_id").GetString().Should().Be("scope-current");
        root.GetProperty("team_id").GetString().Should().Be("team-alpha");
        root.GetProperty("entry_member_id").GetString().Should().Be("m-entry");
    }

    [Fact]
    public async Task TeamQueryTools_WhenModelSuppliesScope_ShouldRejectUnknownArgumentAndNotCallPort()
    {
        var teamQueryPort = new RecordingTeamQueryPort();
        var tool = await DiscoverListTeamsToolAsync(teamQueryPort);

        using var _ = PushContext(scopeId: "scope-context", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"scope_id":"scope-model"}""");

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("Unknown argument: scope_id");
        teamQueryPort.ListCallCount.Should().Be(0);
    }

    [Fact]
    public async Task TeamQueryTools_WhenScopeMissing_ShouldReturnStructuredErrorAndNotCallPort()
    {
        var teamQueryPort = new RecordingTeamQueryPort();
        var tool = await DiscoverGetTeamToolAsync(teamQueryPort);

        using var _ = PushContext(scopeId: null, ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"team_id":"team-alpha"}""");

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        teamQueryPort.GetCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetTeam_WhenMissing_ShouldReturnStructuredNotFound()
    {
        var teamQueryPort = new RecordingTeamQueryPort { GetResult = null };
        var tool = await DiscoverGetTeamToolAsync(teamQueryPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"team_id":"team-missing"}""");

        ErrorCode(output).Should().Be("team_not_found");
    }

    [Fact]
    public async Task ListMembers_ShouldCallReadPortWithCallerScopeTeamAndPage()
    {
        var memberQueryPort = new RecordingMemberQueryPort();
        var tool = await DiscoverListMembersToolAsync(memberQueryPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
              "page_size": 10,
              "page_token": "page-3"
            }
            """);

        memberQueryPort.LastListScopeId.Should().Be("scope-current");
        memberQueryPort.LastListPage.Should().Be(new StudioMemberRosterPageRequest(10, "page-3", "team-alpha"));

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("scope_id").GetString().Should().Be("scope-current");
        var member = root.GetProperty("members")[0];
        member.GetProperty("member_id").GetString().Should().Be("m-alpha");
        member.GetProperty("published_service_id").GetString().Should().Be("svc-alpha");
        member.GetProperty("implementation_ref").GetProperty("workflow_id").GetString().Should().Be("wf-alpha");
        member.GetProperty("team_id").GetString().Should().Be("team-alpha");
        member.GetProperty("member_url").GetString().Should().Be("/api/scopes/scope-current/members/m-alpha");
        member.GetProperty("binding_url").GetString().Should().Be("/api/scopes/scope-current/members/m-alpha/binding");
        root.GetProperty("next_page_token").GetString().Should().Be("next-members");
    }

    [Fact]
    public async Task GetMember_ShouldCallReadPortWithCallerScopeAndMemberId()
    {
        var memberQueryPort = new RecordingMemberQueryPort();
        var tool = await DiscoverGetMemberToolAsync(memberQueryPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"member_id":"m-alpha"}""");

        memberQueryPort.LastGetScopeId.Should().Be("scope-current");
        memberQueryPort.LastGetMemberId.Should().Be("m-alpha");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var summary = root.GetProperty("summary");
        summary.GetProperty("member_id").GetString().Should().Be("m-alpha");
        summary.GetProperty("published_service_id").GetString().Should().Be("svc-alpha");
        root.GetProperty("implementation_ref").GetProperty("workflow_id").GetString().Should().Be("wf-alpha");
        root.GetProperty("last_binding").GetProperty("published_service_id").GetString().Should().Be("svc-alpha");
        root.GetProperty("last_binding").GetProperty("revision_id").GetString().Should().Be("rev-alpha");
    }

    [Fact]
    public async Task MemberQueryTools_WhenModelSuppliesScope_ShouldRejectUnknownArgumentAndNotCallPort()
    {
        var memberQueryPort = new RecordingMemberQueryPort();
        var tool = await DiscoverListMembersToolAsync(memberQueryPort);

        using var _ = PushContext(scopeId: "scope-context", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"scope_id":"scope-model"}""");

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("Unknown argument: scope_id");
        memberQueryPort.ListCallCount.Should().Be(0);
    }

    [Fact]
    public async Task MemberQueryTools_WhenScopeMissing_ShouldReturnStructuredErrorAndNotCallPort()
    {
        var memberQueryPort = new RecordingMemberQueryPort();
        var tool = await DiscoverGetMemberToolAsync(memberQueryPort);

        using var _ = PushContext(scopeId: null, ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"member_id":"m-alpha"}""");

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        memberQueryPort.GetCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetMember_WhenMissing_ShouldReturnStructuredNotFound()
    {
        var memberQueryPort = new RecordingMemberQueryPort { GetResult = null };
        var tool = await DiscoverGetMemberToolAsync(memberQueryPort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"member_id":"m-missing"}""");

        ErrorCode(output).Should().Be("member_not_found");
    }

    [Fact]
    public async Task ListSchedules_ShouldCallReadPortWithCallerScopeTeamMemberAndPage()
    {
        var port = new RecordingMemberAutomationQueryPort();
        var tool = await DiscoverListSchedulesToolAsync(port);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
              "member_id": "m-alpha",
              "page_size": 15,
              "page_token": "page-4",
              "include_total_count": true
            }
            """);

        port.LastScopeId.Should().Be("scope-current");
        port.LastTeamId.Should().Be("team-alpha");
        port.LastMemberId.Should().Be("m-alpha");
        port.LastTake.Should().Be(15);
        port.LastCursor.Should().Be("page-4");
        port.LastIncludeTotalCount.Should().BeTrue();

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("scope_id").GetString().Should().Be("scope-current");
        root.GetProperty("team_id").GetString().Should().Be("team-alpha");
        root.GetProperty("member_id").GetString().Should().Be("m-alpha");
        var schedule = root.GetProperty("schedules")[0];
        schedule.GetProperty("schedule_id").GetString().Should().Be("sched-alpha");
        schedule.GetProperty("authorization_status").GetString().Should().Be("active");
        schedule.GetProperty("schedule_url").GetString().Should().Be(
            "/api/schedules/sched-alpha?ownerKind=studio_member_automation&ownerScopeId=scope-current&ownerTeamId=team-alpha&ownerMemberId=m-alpha");
        schedule.TryGetProperty("team_automation_lifecycle_status", out var lifecycleStatus).Should().BeFalse();
        schedule.GetProperty("state_version").GetInt64().Should().Be(42);
        root.GetProperty("next_page_token").GetString().Should().Be("next-schedules");
        root.GetProperty("total_count").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task GetSchedule_ShouldCallReadPortWithCallerScopeTeamMemberAndScheduleId()
    {
        var port = new RecordingMemberAutomationQueryPort();
        var tool = await DiscoverGetScheduleToolAsync(port);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync(
            """{"team_id":"team-alpha","member_id":"m-alpha","schedule_id":"sched-alpha"}""");

        port.LastScopeId.Should().Be("scope-current");
        port.LastTeamId.Should().Be("team-alpha");
        port.LastMemberId.Should().Be("m-alpha");
        port.LastScheduleId.Should().Be("sched-alpha");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("scope_id").GetString().Should().Be("scope-current");
        root.GetProperty("team_id").GetString().Should().Be("team-alpha");
        root.GetProperty("member_id").GetString().Should().Be("m-alpha");
        root.GetProperty("schedule_id").GetString().Should().Be("sched-alpha");
        root.GetProperty("published_service_id").GetString().Should().Be("svc-alpha");
        root.GetProperty("authorization_status").GetString().Should().Be("active");
        root.TryGetProperty("recent_fires", out var recentFires).Should().BeFalse();
    }

    [Fact]
    public async Task GetSchedule_WhenMemberIdMissing_ShouldReturnInvalidArgumentsAndNotCallPort()
    {
        var port = new RecordingMemberAutomationQueryPort();
        var tool = await DiscoverGetScheduleToolAsync(port);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"team_id":"team-alpha","schedule_id":"sched-alpha"}""");

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("member_id is required.");
        port.GetCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ListSchedules_WhenTeamIdMissing_ShouldReturnInvalidArgumentsAndNotCallPort()
    {
        var port = new RecordingMemberAutomationQueryPort();
        var tool = await DiscoverListSchedulesToolAsync(port);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"member_id":"m-alpha"}""");

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("team_id is required.");
        port.ListCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ListSchedules_WhenMemberIdMissing_ShouldListTeamWideSchedules()
    {
        var port = new RecordingMemberAutomationQueryPort();
        var tool = await DiscoverListSchedulesToolAsync(port);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"team_id":"team-alpha"}""");

        port.LastScopeId.Should().Be("scope-current");
        port.LastTeamId.Should().Be("team-alpha");
        port.LastMemberId.Should().BeNull();

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.TryGetProperty("member_id", out var memberIdProperty).Should().BeFalse();
        var schedule = root.GetProperty("schedules")[0];
        schedule.GetProperty("member_id").GetString().Should().Be("m-alpha");
    }

    [Fact]
    public async Task ScheduleQueryTools_WhenModelSuppliesScope_ShouldRejectUnknownArgumentAndNotCallPort()
    {
        var port = new RecordingMemberAutomationQueryPort();
        var tool = await DiscoverListSchedulesToolAsync(port);

        using var _ = PushContext(scopeId: "scope-context", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync(
            """{"scope_id":"scope-model","team_id":"team-alpha","member_id":"m-alpha"}""");

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("Unknown argument: scope_id");
        port.ListCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleQueryTools_WhenScopeMissing_ShouldReturnStructuredErrorAndNotCallPort()
    {
        var port = new RecordingMemberAutomationQueryPort();
        var tool = await DiscoverGetScheduleToolAsync(port);

        using var _ = PushContext(scopeId: null, ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync(
            """{"team_id":"team-alpha","member_id":"m-alpha","schedule_id":"sched-alpha"}""");

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        port.GetCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSchedule_WhenMissing_ShouldReturnStructuredNotFound()
    {
        var port = new RecordingMemberAutomationQueryPort { GetResult = null };
        var tool = await DiscoverGetScheduleToolAsync(port);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync(
            """{"team_id":"team-alpha","member_id":"m-alpha","schedule_id":"sched-missing"}""");

        ErrorCode(output).Should().Be("schedule_not_found");
    }

    [Fact]
    public async Task ScheduleQueryTools_WhenCanceled_ShouldRethrowCancellation()
    {
        var port = new RecordingMemberAutomationQueryPort
        {
            Failure = new OperationCanceledException(),
        };
        var tool = await DiscoverListSchedulesToolAsync(port);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var act = () => tool.ExecuteAsync("""{"team_id":"team-alpha","member_id":"m-alpha"}""");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ScheduleQueryTools_WhenProviderFails_ShouldReturnSafeStructuredError()
    {
        var port = new RecordingMemberAutomationQueryPort
        {
            Failure = new IOException("sensitive provider detail"),
        };
        var tool = await DiscoverGetScheduleToolAsync(port);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync(
            """{"team_id":"team-alpha","member_id":"m-alpha","schedule_id":"sched-alpha"}""");

        ErrorCode(output).Should().Be("schedule_query_failed");
        ErrorMessage(output).Should().Be("Studio schedule query failed: IOException");
        output.Should().NotContain("sensitive provider detail");
    }

    [Fact]
    public async Task ToolSource_WhenScheduleMemberWorkflowPortRegistered_ShouldDiscoverScheduleMemberWorkflowTool()
    {
        var source = new ScheduleStudioMemberWorkflowToolSource(new RecordingMemberWorkflowSchedulePort());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be(ScheduleMemberWorkflowToolName);
    }

    [Fact]
    public async Task ToolSource_WhenScheduleMemberWorkflowPortMissing_ShouldNotDiscoverScheduleMemberWorkflowTool()
    {
        var source = new ScheduleStudioMemberWorkflowToolSource();

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task BindMemberWorkflow_ShouldCallBindingPortWithCallerScope()
    {
        var bindingPort = new RecordingMemberWorkflowBindingPort();
        var tool = await DiscoverBindMemberWorkflowToolAsync(bindingPort);

        using var context = PushContext(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "workflow_yaml": "name: team_workflow\nsteps: []\n",
              "workflow_id": "workflow-alpha"
            }
            """);

        bindingPort.LastRequest.Should().NotBeNull();
        bindingPort.LastRequest!.ScopeId.Should().Be("scope-alpha");
        bindingPort.LastRequest.MemberId.Should().Be("member-alpha");
        bindingPort.LastRequest.WorkflowYaml.Should().Contain("name: team_workflow");
        bindingPort.LastRequest.WorkflowId.Should().Be("workflow-alpha");
        bindingPort.LastRequest.CapabilityAdmission.Should().NotBeNull();
        bindingPort.LastRequest.CapabilityAdmission!.CallerId.Should().Be("nyx-user-alpha");
        bindingPort.LastRequest.CapabilityAdmission.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be("access-token-1");
        bindingPort.LastRequest.CapabilityAdmission.NyxIdOrganizationBearerToken.Should().Be("org-token");
        bindingPort.LastRequest.CapabilityAdmission.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Interactive);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("scope_id").GetString().Should().Be("scope-alpha");
        root.GetProperty("member_id").GetString().Should().Be("member-alpha");
        root.GetProperty("operation").GetString().Should().Be(StudioMemberWorkflowBindingOperationNames.Bind);
        root.GetProperty("status").GetString().Should().Be("accepted");
        root.GetProperty("binding_run_id").GetString().Should().Be("binding-run-1");
        root.GetProperty("ack_stage").GetString().Should().Be("dispatch_accepted");
        root.GetProperty("binding_run_role").GetString().Should().Be("candidate");
        root.GetProperty("binding_run_url").GetString()
            .Should().Be("/api/scopes/scope-alpha/members/member-alpha/binding-runs/binding-run-1");
        root.GetProperty("member_workflow_url").GetString()
            .Should().Be("/api/scopes/scope-alpha/members/member-alpha/binding");
        root.GetProperty("workflow_id").GetString().Should().Be("workflow-alpha");
        root.TryGetProperty("revision_id", out _).Should().BeFalse();
        root.TryGetProperty("service_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task BindMemberWorkflow_WhenOwnerScopePresent_ShouldCallPortWithOwnerScope()
    {
        var bindingPort = new RecordingMemberWorkflowBindingPort();
        var tool = await DiscoverBindMemberWorkflowToolAsync(bindingPort);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "owner-alpha",
            accessToken: "access-token-1",
            ownerScopeId: "owner-scope",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "workflow_yaml": "name: demo\n"
            }
            """);

        bindingPort.LastRequest.Should().NotBeNull();
        bindingPort.LastRequest!.ScopeId.Should().Be("owner-scope");

        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("scope_id").GetString().Should().Be("owner-scope");
    }

    [Fact]
    public async Task BindMemberWorkflow_WhenScopeMissing_ShouldReturnStructuredErrorAndNotCallPort()
    {
        var bindingPort = new RecordingMemberWorkflowBindingPort();
        var tool = await DiscoverBindMemberWorkflowToolAsync(bindingPort);

        using var _ = PushContext(scopeId: null, ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"member_id":"member-alpha","workflow_yaml":"name: demo\n"}""");

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        bindingPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task BindMemberWorkflow_WhenOnlyOwnerSubjectIdentifiesCaller_ShouldRejectAndNotCallPort()
    {
        var bindingPort = new RecordingMemberWorkflowBindingPort();
        var tool = await DiscoverBindMemberWorkflowToolAsync(bindingPort);

        using var _ = PushContext(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"member_id":"m-alpha","workflow_yaml":"name: demo\n"}""");

        ErrorCode(output).Should().Be("caller_identity_unavailable");
        bindingPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task BindMemberWorkflow_WhenModelSuppliesScope_ShouldRejectUnknownArgumentAndNotCallPort()
    {
        var bindingPort = new RecordingMemberWorkflowBindingPort();
        var tool = await DiscoverBindMemberWorkflowToolAsync(bindingPort);

        using var _ = PushContext(scopeId: "scope-context", ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "scope_id": "scope-model",
              "member_id": "member-alpha",
              "workflow_yaml": "name: demo\n"
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("Unknown argument: scope_id");
        bindingPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task BindMemberWorkflow_ShouldUseSharedCreateScopedResourceApprovalPolicy()
    {
        var tool = await DiscoverBindMemberWorkflowToolAsync(new RecordingMemberWorkflowBindingPort());

        tool.ApprovalMode.Should().Be(ToolApprovalPolicies.CreateScopedResource);
        tool.Should().NotBeAssignableTo<IAgentToolCapabilityDescriptor>();
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_ShouldCallSchedulePortWithCallerScopeAndSubject()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai",
              "prompt": "run digest",
              "display_name": "Daily digest"
            }
            """);

        schedulePort.LastRequest.Should().NotBeNull();
        schedulePort.LastRequest!.ScopeId.Should().Be("scope-current");
        schedulePort.LastRequest.MemberId.Should().Be("member-alpha");
        schedulePort.LastRequest.ScheduleCron.Should().Be("0 9 * * *");
        schedulePort.LastRequest.ScheduleTimezone.Should().Be("Asia/Shanghai");
        schedulePort.LastRequest.Prompt.Should().Be("run digest");
        schedulePort.LastRequest.DisplayName.Should().Be("Daily digest");
        schedulePort.LastRequest.CallerSubjectExternalUserId.Should().Be("nyx-user-alpha");
        schedulePort.LastRequest.OperationId.Should()
            .MatchRegex("^studio-member-workflow-create:[0-9a-f]{64}$");
        schedulePort.LastRequest.IdempotencyKey.Should()
            .MatchRegex("^studio-member-workflow-schedule:[0-9a-f]{64}$");
        schedulePort.LastRequest.CredentialProvisioningKind.Should()
            .Be("dedicated_scheduled_invocation_agent_key");
        schedulePort.LastRequest.ConfirmedPolicyVersion.Should()
            .Be(RecordingMemberWorkflowSchedulePort.PolicyVersion);
        schedulePort.PreflightRequests.Should().ContainSingle();
        schedulePort.WritePreflightRequests.Should().ContainSingle();
        schedulePort.WritePreflightRequests[0].ConfirmedPolicyVersion.Should().BeNull();
        schedulePort.WritePreflightRequests[0].OperationId.Should().Be(schedulePort.LastRequest.OperationId);
        schedulePort.WritePreflightRequests[0].IdempotencyKey.Should().Be(schedulePort.LastRequest.IdempotencyKey);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("scope_id").GetString().Should().Be("scope-current");
        root.GetProperty("member_id").GetString().Should().Be("member-alpha");
        root.GetProperty("schedule_id").GetString().Should().Be("schedule-member-1");
        root.GetProperty("published_service_id").GetString().Should().Be("published-member-1");
        root.GetProperty("observatory_url").GetString().Should().Be("/workflow/observatory");
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenOwnerScopePresent_ShouldCallPortWithOwnerScope()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            ownerScopeId: "owner-scope",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        schedulePort.LastRequest.Should().NotBeNull();
        schedulePort.LastRequest!.ScopeId.Should().Be("owner-scope");

        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("scope_id").GetString().Should().Be("owner-scope");
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenTypedNyxIdAuthorityPresent_ShouldUseItAsAuthorizationOwner()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "fallback-owner",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-typed", "typed-user"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().BeNull();
        schedulePort.LastRequest.Should().NotBeNull();
        var owner = schedulePort.LastRequest!.AuthenticatedOwner;
        owner.Owner.OwnerSubject.Should().Be("typed-user");
        owner.SubjectPlatform.Should().Be("nyxid");
        owner.SubjectTenant.Should().Be("tenant-typed");
        owner.SubjectExternalUserId.Should().Be("typed-user");
        owner.VerifiedBindingId.Should().Be("binding-alpha");
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenChannelSenderContextPresent_ShouldUseBindingBackedChannelSubject()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "fallback-owner",
            accessToken: "access-token-1",
            ownerScopeId: "owner-scope",
            senderBindingId: "binding-lark",
            senderNyxUserId: "nyx-lark-user",
            senderTenant: "tenant-lark",
            channelPlatform: "lark",
            channelSenderId: "ou_sender");
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().BeNull();
        schedulePort.LastRequest.Should().NotBeNull();
        schedulePort.LastRequest!.ScopeId.Should().Be("owner-scope");
        var owner = schedulePort.LastRequest.AuthenticatedOwner;
        owner.Owner.OwnerSubject.Should().Be("nyx-lark-user");
        owner.SubjectPlatform.Should().Be("lark");
        owner.SubjectTenant.Should().Be("tenant-lark");
        owner.SubjectExternalUserId.Should().Be("ou_sender");
        owner.VerifiedBindingId.Should().Be("binding-lark");
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenTypedLarkAuthorityPresent_ShouldKeepNyxIdOwnerSeparateFromChannelSubject()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "fallback-owner",
            accessToken: "access-token-1",
            ownerScopeId: "owner-scope",
            senderBindingId: "binding-lark",
            senderNyxUserId: "nyx-lark-user",
            senderTenant: "tenant-binding",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("lark", "tenant-authority", "ou_authority"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().BeNull();
        schedulePort.LastRequest.Should().NotBeNull();
        schedulePort.LastRequest!.ScopeId.Should().Be("owner-scope");
        var owner = schedulePort.LastRequest.AuthenticatedOwner;
        owner.Owner.OwnerSubject.Should().Be("nyx-lark-user");
        owner.SubjectPlatform.Should().Be("lark");
        owner.SubjectTenant.Should().Be("tenant-authority");
        owner.SubjectExternalUserId.Should().Be("ou_authority");
        owner.VerifiedBindingId.Should().Be("binding-lark");
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenTypedLarkAuthorityHasNoNyxIdOwner_ShouldFailBeforePreflight()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "fallback-owner",
            accessToken: "access-token-1",
            ownerScopeId: "owner-scope",
            senderBindingId: "binding-lark",
            senderTenant: "tenant-lark",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("lark", "tenant-authority", "ou_authority"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().Be("caller_subject_unavailable");
        output.Should().NotContain("ou_authority");
        schedulePort.PreflightRequests.Should().BeEmpty();
        schedulePort.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenBearerMissingAndTypedNyxIdAuthorityPresent_ShouldDeferTokenIssuanceToPort()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "fallback-owner",
            accessToken: null,
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-typed", "typed-user"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().BeNull();
        schedulePort.LastRequest.Should().NotBeNull();
        schedulePort.LastRequest!.ProvisioningBearerToken.Should().BeNull();
        schedulePort.LastRequest.AuthenticatedOwner.SubjectPlatform.Should().Be("nyxid");
        schedulePort.LastRequest.AuthenticatedOwner.SubjectTenant.Should().Be("tenant-typed");
        schedulePort.LastRequest.AuthenticatedOwner.SubjectExternalUserId.Should().Be("typed-user");
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenUncertainCallIsRetried_ShouldReuseStableOperationIdentity()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);
        const string arguments = """
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai",
              "prompt": "run digest",
              "display_name": "Daily digest"
            }
            """;

        using (PushContext(
                   scopeId: "scope-current",
                   ownerSubject: "owner-1",
                   accessToken: "access-token-1",
                   requestId: "transport-request-1",
                   callId: "tool-call-1",
                   idempotencyKey: "caller-operation-1",
                   nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha")))
        {
            ErrorCode(await tool.ExecuteAsync(arguments)).Should().BeNull();
        }

        using (PushContext(
                   scopeId: "scope-current",
                   ownerSubject: "owner-1",
                   accessToken: "access-token-1",
                   requestId: "transport-request-2",
                   callId: "tool-call-2",
                   idempotencyKey: "caller-operation-1",
                   nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha")))
        {
            ErrorCode(await tool.ExecuteAsync(arguments)).Should().BeNull();
        }

        schedulePort.CreateRequests.Should().HaveCount(2);
        schedulePort.CreateRequests.Select(static request => request.OperationId).Distinct()
            .Should().ContainSingle();
        schedulePort.CreateRequests.Select(static request => request.IdempotencyKey).Distinct()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_Identity_ShouldDistinguishResourcesButNotPayloadDrift()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();

        await ExecuteScheduleMemberWorkflowAsync(
            schedulePort,
            "scope-alpha",
            "member-alpha",
            "0 9 * * *",
            "run digest",
            "Daily digest");
        await ExecuteScheduleMemberWorkflowAsync(
            schedulePort,
            "scope-beta",
            "member-alpha",
            "0 9 * * *",
            "run digest",
            "Daily digest");
        await ExecuteScheduleMemberWorkflowAsync(
            schedulePort,
            "scope-alpha",
            "member-beta",
            "0 9 * * *",
            "run digest",
            "Daily digest");
        await ExecuteScheduleMemberWorkflowAsync(
            schedulePort,
            "scope-alpha",
            "member-alpha",
            "0 10 * * *",
            "run digest",
            "Daily digest");
        await ExecuteScheduleMemberWorkflowAsync(
            schedulePort,
            "scope-alpha",
            "member-alpha",
            "0 9 * * *",
            "run another digest",
            "Daily digest");

        schedulePort.CreateRequests.Should().HaveCount(5);
        schedulePort.CreateRequests.Take(3).Select(static request => request.OperationId)
            .Should().OnlyHaveUniqueItems();
        schedulePort.CreateRequests.Take(3).Select(static request => request.IdempotencyKey)
            .Should().OnlyHaveUniqueItems();
        schedulePort.CreateRequests[3].OperationId.Should().Be(schedulePort.CreateRequests[0].OperationId);
        schedulePort.CreateRequests[4].OperationId.Should().Be(schedulePort.CreateRequests[0].OperationId);
        schedulePort.CreateRequests[3].IdempotencyKey.Should().Be(schedulePort.CreateRequests[0].IdempotencyKey);
        schedulePort.CreateRequests[4].IdempotencyKey.Should().Be(schedulePort.CreateRequests[0].IdempotencyKey);
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenInvocationIdentityIsMissing_ShouldFailBeforePreflight()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);
        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            requestId: null,
            callId: null,
            idempotencyKey: null,
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));

        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().Be("operation_identity_unavailable");
        schedulePort.PreflightRequests.Should().BeEmpty();
        schedulePort.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenPreflightOmitsPolicyVersion_ShouldFailBeforeCreate()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort
        {
            PreflightResult = new StudioMemberWorkflowAuthorizationResult(
                true,
                new ScheduledInvocationAuthorizationPlan
                {
                    PermissionDigest = "permission-digest-alpha",
                    CredentialPolicy = new ScheduledInvocationCredentialPolicy(),
                },
                ScheduledInvocationAuthorizationFailureCode.Unspecified,
                string.Empty),
        };
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().Be("authorization_plan_invalid");
        ErrorMessage(output).Should().Contain("credential_policy.policy_version");
        schedulePort.CreateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenPreflightFails_ShouldReturnFailureAndNotCreateSchedule()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort
        {
            PreflightResult = new StudioMemberWorkflowAuthorizationResult(
                false,
                null,
                ScheduledInvocationAuthorizationFailureCode.SnapshotStale,
                "nyxid_catalog_snapshot_stale"),
        };
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().Be(nameof(ScheduledInvocationAuthorizationFailureCode.SnapshotStale));
        ErrorMessage(output).Should().Be("nyxid_catalog_snapshot_stale");
        schedulePort.CreateCallCount.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(ScheduleMemberWorkflowWritePreflightExceptionCases))]
    public async Task ScheduleMemberWorkflow_WhenWritePreflightThrowsKnownAuthorizationRefreshException_ShouldReturnStableError(
        Exception exception,
        string expectedCode,
        string expectedMessage)
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort
        {
            WritePreflightException = exception,
        };
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().Be(expectedCode);
        ErrorMessage(output).Should().Be(expectedMessage);
        schedulePort.WritePreflightRequests.Should().ContainSingle();
        schedulePort.CreateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenCreateThrowsKnownAuthorizationRefreshException_ShouldReturnStableError()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort
        {
            CreateException = new StudioMemberAutomationCatalogRefreshUnavailableException(),
        };
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().Be("authorization_catalog_refresh_unavailable");
        ErrorMessage(output).Should().Be("The authorization catalog could not be refreshed. Retry this request.");
        schedulePort.WritePreflightRequests.Should().ContainSingle();
        schedulePort.CreateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenAuthorizationPlanMismatchDuringCreate_ShouldReturnSanitizedReason()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort
        {
            CreateException = new StudioMemberAutomationPlanConflictException(
                "authorization_plan_changed",
                "private authorization planner detail owner-alpha node-a",
                ScheduledAuthorizationPlanMismatchReason.AllowedNodeIdsMismatch),
        };
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().Be("authorization_plan_changed");
        ErrorMessage(output).Should().Be("The authorization plan changed. Run schedule preflight again before retrying.");
        ErrorAuthorizationPlanMismatchReason(output).Should().Be("allowed_node_ids_mismatch");
        output.Should()
            .NotContain("private authorization planner detail")
            .And.NotContain("owner-alpha")
            .And.NotContain("node-a");
        schedulePort.WritePreflightRequests.Should().ContainSingle();
        schedulePort.CreateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenScopeMissing_ShouldReturnStructuredErrorAndNotCallPort()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(scopeId: null, ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"member_id":"member-alpha","schedule_cron":"0 9 * * *","schedule_timezone":"Asia/Shanghai"}""");

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        schedulePort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenNyxIdCallerSubjectMissing_ShouldReturnStructuredErrorAndNotCallPort()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(scopeId: "scope-current", ownerSubject: null, accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""{"member_id":"member-alpha","schedule_cron":"0 9 * * *","schedule_timezone":"Asia/Shanghai"}""");

        ErrorCode(output).Should().Be("caller_subject_unavailable");
        schedulePort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenLarkOwnerSubjectIsTransformedScopeAndSenderNyxUserMissing_ShouldFailBeforePreflight()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "rscope_transformed_owner",
            accessToken: "access-token-1",
            ownerScopeId: "rscope_transformed_owner",
            senderBindingId: "binding-lark",
            senderTenant: "tenant-lark",
            channelPlatform: "lark",
            channelSenderId: "ou_sender",
            channelRegistrationScopeId: "rscope_transformed_owner");
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "m-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().Be("caller_subject_unavailable");
        output.Should().NotContain("rscope_transformed_owner");
        schedulePort.PreflightRequests.Should().BeEmpty();
        schedulePort.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenAuthorizationResolutionFails_ShouldLogPrivacySafeDiagnostics()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var logger = new RecordingLogger<ScheduleStudioMemberWorkflowTool>();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort, logger);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "owner-sensitive",
            accessToken: "access-token-sensitive",
            ownerScopeId: "owner-scope-sensitive",
            senderBindingId: "binding-sensitive",
            senderTenant: "tenant-sensitive",
            channelPlatform: "lark-sensitive",
            channelSenderId: "ou-sensitive",
            channelRegistrationScopeId: "channel-scope-sensitive");
        var output = await tool.ExecuteAsync("""
            {
              "member_id": "m-sensitive",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().Be("caller_subject_unavailable");
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should()
            .Contain("code=caller_subject_unavailable")
            .And.Contain("has_typed_authority=False")
            .And.Contain("has_binding_id=True")
            .And.Contain("has_sender_nyx_user_id=False")
            .And.Contain("has_sender_tenant=True")
            .And.Contain("has_channel_platform=True")
            .And.Contain("has_channel_sender_id=True")
            .And.Contain("has_owner_subject=True")
            .And.Contain("has_owner_scope_id=True");
        entry.Message.Should()
            .NotContain("owner-sensitive")
            .And.NotContain("access-token-sensitive")
            .And.NotContain("owner-scope-sensitive")
            .And.NotContain("binding-sensitive")
            .And.NotContain("tenant-sensitive")
            .And.NotContain("lark-sensitive")
            .And.NotContain("ou-sensitive")
            .And.NotContain("channel-scope-sensitive")
            .And.NotContain("m-sensitive");
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenSenderBindingMissing_ShouldReturnAuthorizationContextUnavailable()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            senderBindingId: null,
            senderNyxUserId: "nyx-user-alpha",
            senderTenant: "tenant-alpha",
            channelPlatform: "lark",
            channelSenderId: "ou-alpha");
        var output = await tool.ExecuteAsync("""{"member_id":"member-alpha","schedule_cron":"0 9 * * *","schedule_timezone":"Asia/Shanghai"}""");

        ErrorCode(output).Should().Be("authenticated_owner_context_unavailable");
        schedulePort.PreflightRequests.Should().BeEmpty();
        schedulePort.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenModelSuppliesScope_ShouldRejectUnknownArgumentAndNotCallPort()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-context",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "scope_id": "scope-model",
              "member_id": "member-alpha",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai"
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("Unknown argument: scope_id");
        schedulePort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_WhenRequiredArgumentsMissing_ShouldReturnInvalidArguments()
    {
        var schedulePort = new RecordingMemberWorkflowSchedulePort();
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);

        using var _ = PushContext(
            scopeId: "scope-current",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""{"member_id":"member-alpha","schedule_cron":"0 9 * * *"}""");

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("schedule_timezone is required.");
        schedulePort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScheduleMemberWorkflow_ShouldUseSharedCreateScopedResourceApprovalPolicy()
    {
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(new RecordingMemberWorkflowSchedulePort());

        tool.ApprovalMode.Should().Be(ToolApprovalPolicies.CreateScopedResource);
        tool.Should().NotBeAssignableTo<IAgentToolCapabilityDescriptor>();
    }

    [Fact]
    public async Task ProvisionWorkflowSchedule_ShouldUseSharedCreateScopedResourceApprovalPolicy()
    {
        var tool = await DiscoverToolAsync(new RecordingProvisioningPort());

        tool.ApprovalMode.Should().Be(ToolApprovalPolicies.CreateScopedResource);
        tool.Should().NotBeAssignableTo<IAgentToolCapabilityDescriptor>();
    }

    [Fact]
    public async Task CreateResultReceipt_WithAcceptedSchedule_ShouldReturnSuccessReceipt()
    {
        var tool = await DiscoverToolAsync(new RecordingProvisioningPort());
        var resultJson = JsonSerializer.Serialize(new
        {
            status = "accepted",
            member_id = "member-1",
            scope_id = "scope-1",
            team_id = "team-alpha",
            schedule_id = "schedule-1",
            studio_url = "/scopes/scope-1/teams/team-alpha/members/member-1/workflow",
            observatory_url = "/workflow/observatory",
        });

        var receipt = tool.CreateResultReceipt("call-1", ScheduleToolName, "{}", resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.CallId.Should().Be("call-1");
        receipt.ToolName.Should().Be(ScheduleToolName);
        receipt.SideEffectKind.Should().Be("studio.workflow.schedule.provision");
        receipt.SubjectKind.Should().Be("studio_member_workflow_schedule");
        receipt.SubjectId.Should().Be("schedule-1");
        receipt.ResultJson.Should().Be(resultJson);
    }

    [Fact]
    public async Task CreateResultReceipt_WithToolError_ShouldReturnErrorReceipt()
    {
        var tool = await DiscoverToolAsync(new RecordingProvisioningPort());
        var resultJson = """
            {"error":{"code":"caller_identity_unavailable","message":"Verified NyxID caller identity is required in AgentToolRequestContext."}}
            """;

        var receipt = tool.CreateResultReceipt("call-1", ScheduleToolName, "{}", resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("caller_identity_unavailable");
        receipt.ErrorMessage.Should().Be("Verified NyxID caller identity is required in AgentToolRequestContext.");
        receipt.SideEffectKind.Should().Be("studio.workflow.schedule.provision");
        receipt.ResultJson.Should().Be(resultJson);
    }

    [Fact]
    public async Task Execute_ShouldMapArgumentsAndContextOntoProvisioningRequest()
    {
        var port = new RecordingProvisioningPort(new WorkflowScheduleProvisioningResult(
            MemberId: "member-1",
            ScopeId: "scope-1",
            TeamId: "team-alpha",
            BindingStatus: "accepted",
            ObservatoryUrl: "/workflow/observatory",
            StudioUrl: "/scopes/scope-1/teams/team-alpha/members/member-1/workflow")
        {
            ScheduleId = "schedule-1",
            BindingRunId = "bind-run-1",
        });
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
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
        request.ScopeId.Should().Be("scope-alpha");
        request.GetType().GetProperty("TeamId")!.GetValue(request).Should().Be("team-alpha");
        request.DisplayName.Should().Be("Daily Tech News");
        request.WorkflowYaml.Should().Contain("name: daily-tech-news");
        request.Prompt.Should().Be("summarize today's tech news");
        request.ScheduleCron.Should().Be("0 9 * * *");
        request.ScheduleTimezone.Should().Be("Asia/Shanghai");
        request.RunImmediately.Should().BeFalse();
        // Caller identity is taken from the tool execution context (W1-threaded), not arguments.
        request.CallerSubjectExternalUserId.Should().Be("nyx-user-alpha");
        request.CapabilityAdmission.Should().NotBeNull();
        request.CapabilityAdmission!.CallerId.Should().Be("nyx-user-alpha");
        request.CapabilityAdmission.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be("access-token-1");
        request.CapabilityAdmission.NyxIdOrganizationBearerToken.Should().Be("org-token");
        request.CapabilityAdmission.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Durable);
        request.AuthenticatedOwner.Should().NotBeNull();
        request.ProvisioningBearerToken.Should().Be("access-token-1");
        request.ScheduleOperationId.Should()
            .MatchRegex("^studio-workflow-provision-create:[0-9a-f]{64}$");
        request.ScheduleIdempotencyKey.Should()
            .MatchRegex("^studio-workflow-provision-schedule:[0-9a-f]{64}$");

        // Result surfaces the schedule + Observatory link.
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("accepted");
        root.GetProperty("member_id").GetString().Should().Be("member-1");
        root.GetProperty("team_id").GetString().Should().Be("team-alpha");
        root.GetProperty("schedule_id").GetString().Should().Be("schedule-1");
        root.GetProperty("studio_url").GetString().Should()
            .Be("/scopes/scope-1/teams/team-alpha/members/member-1/workflow");
        root.GetProperty("observatory_url").GetString().Should().Be("/workflow/observatory");
    }

    [Theory]
    [InlineData(AgentToolNyxIdCredentialKind.ProxyDelegation)]
    [InlineData(AgentToolNyxIdCredentialKind.Unspecified)]
    public async Task Execute_WhenCredentialIsNotSourceReadable_ShouldOmitCapabilityCallerCredential(
        AgentToolNyxIdCredentialKind credentialKind)
    {
        var port = new RecordingProvisioningPort();
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            accessToken: "caller-token",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"),
            nyxIdCredentialKind: credentialKind);
        await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
              "workflow_yaml": "name: daily-tech-news\nroles: []\n",
              "display_name": "Daily Tech News",
              "prompt": "summarize today's tech news",
              "schedule_cron": "0 9 * * *",
              "schedule_timezone": "Asia/Shanghai",
              "run_immediately": false
            }
            """);

        port.LastRequest.Should().NotBeNull();
        port.LastRequest!.CapabilityAdmission.Should().NotBeNull();
        port.LastRequest.CapabilityAdmission!.NyxIdCallerCredential.Should().BeNull();
    }

    [Fact]
    public async Task Execute_WhenOwnerScopePresent_ShouldCallPortWithOwnerScope()
    {
        var port = new RecordingProvisioningPort(new WorkflowScheduleProvisioningResult(
            MemberId: "member-1",
            ScopeId: "owner-scope",
            TeamId: "team-alpha",
            BindingStatus: "accepted",
            ObservatoryUrl: "/workflow/observatory",
            StudioUrl: "/scopes/owner-scope/teams/team-alpha/members/member-1/workflow"));
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(
            scopeId: "registration-scope",
            ownerSubject: "owner-alpha",
            accessToken: "access-token-1",
            ownerScopeId: "owner-scope",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        port.LastRequest.Should().NotBeNull();
        port.LastRequest!.ScopeId.Should().Be("owner-scope");

        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("scope_id").GetString().Should().Be("owner-scope");
    }

    [Fact]
    public async Task Execute_WhenRunImmediatelyAbsent_ShouldDefaultToTrue()
    {
        var port = new RecordingProvisioningPort();
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        port.LastRequest.Should().NotBeNull();
        port.LastRequest!.RunImmediately.Should().BeTrue();
        port.LastRequest.ScheduleOperationId.Should().BeNull();
        port.LastRequest.ScheduleIdempotencyKey.Should().BeNull();
    }

    [Fact]
    public async Task Execute_WhenTeamIdMissing_ShouldReturnInvalidArgumentsAndNotCallPort()
    {
        var port = new RecordingProvisioningPort();
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("team_id is required.");
        port.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Execute_WhenScopeMissing_ShouldReturnCallerScopeUnavailableAndNotCallPort()
    {
        var port = new RecordingProvisioningPort();
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(scopeId: null, ownerSubject: "owner-1", accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        port.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Execute_WhenOnlyOwnerSubjectIdentifiesCaller_ShouldRejectAndNotCallPort()
    {
        var port = new RecordingProvisioningPort();
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            accessToken: "access-token-1");
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        ErrorCode(output).Should().Be("caller_identity_unavailable");
        port.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Execute_WhenWorkflowYamlMissing_ShouldReturnInvalidArguments()
    {
        var port = new RecordingProvisioningPort();
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
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
            Throw = new InvalidOperationException(
                "workflow_yaml is not a valid workflow definition: Unsupported workflow YAML root field 'version'."),
        };
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(
            scopeId: "scope-alpha",
            ownerSubject: "owner-alpha",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        ErrorCode(output).Should().Be("invalid_arguments");
        // The message is the load-bearing half of the repair loop: the tool
        // description tells the model to fix the YAML "per the error message",
        // so the parser text naming the rejected key must survive the mapping.
        ErrorMessage(output).Should().Contain("Unsupported workflow YAML root field 'version'");
    }

    [Fact]
    public async Task Execute_Result_ShouldNotCarryAnyLarkOrChannelFields()
    {
        var port = new RecordingProvisioningPort(new WorkflowScheduleProvisioningResult(
            MemberId: "member-1",
            ScopeId: "scope-1",
            TeamId: "team-alpha",
            BindingStatus: "accepted",
            ObservatoryUrl: "/workflow/observatory",
            StudioUrl: "/scopes/scope-1/teams/team-alpha/members/member-1/workflow")
        {
            ScheduleId = "schedule-1",
        });
        var tool = await DiscoverToolAsync(port);

        using var _ = PushContext(
            scopeId: "scope-1",
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
              "workflow_yaml": "name: demo\n",
              "display_name": "Demo"
            }
            """);

        ErrorCode(output).Should().BeNull();
        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
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

    private static async Task<IAgentTool> DiscoverListTeamsToolAsync(IStudioTeamQueryPort teamQueryPort)
    {
        var source = new StudioTeamQueryToolSource(teamQueryPort);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == ListTeamsToolName);
    }

    private static async Task<IAgentTool> DiscoverGetTeamToolAsync(IStudioTeamQueryPort teamQueryPort)
    {
        var source = new StudioTeamQueryToolSource(teamQueryPort);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == GetTeamToolName);
    }

    private static async Task<IAgentTool> DiscoverCreateMemberToolAsync(IStudioMemberProvisioningPort memberPort)
    {
        var source = new CreateStudioMemberToolSource(memberPort);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == CreateMemberToolName);
    }

    private static async Task<IAgentTool> DiscoverCreateMemberWorkflowDraftToolAsync(
        IStudioMemberWorkflowDraftProvisioningPort draftPort)
    {
        var source = new CreateStudioMemberWorkflowDraftToolSource(draftPort);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == CreateMemberWorkflowDraftToolName);
    }

    private static async Task<IAgentTool> DiscoverListMembersToolAsync(IStudioMemberQueryPort memberQueryPort)
    {
        var source = new StudioMemberQueryToolSource(memberQueryPort);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == ListMembersToolName);
    }

    private static async Task<IAgentTool> DiscoverGetMemberToolAsync(IStudioMemberQueryPort memberQueryPort)
    {
        var source = new StudioMemberQueryToolSource(memberQueryPort);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == GetMemberToolName);
    }

    private static async Task<IAgentTool> DiscoverListWorkflowsToolAsync(IStudioMemberQueryPort memberQueryPort)
    {
        var source = new StudioWorkflowQueryToolSource(memberQueryPort);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == ListWorkflowsToolName);
    }

    private static async Task<IAgentTool> DiscoverListSchedulesToolAsync(
        IStudioMemberAutomationQueryPort schedules)
    {
        var source = new StudioScheduleQueryToolSource(schedules);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == ListSchedulesToolName);
    }

    private static async Task<IAgentTool> DiscoverGetScheduleToolAsync(
        IStudioMemberAutomationQueryPort schedules)
    {
        var source = new StudioScheduleQueryToolSource(schedules);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == GetScheduleToolName);
    }

    private static async Task<IAgentTool> DiscoverBindMemberWorkflowToolAsync(IStudioMemberWorkflowBindingPort bindingPort)
    {
        var source = new BindStudioMemberWorkflowToolSource(bindingPort);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == BindMemberWorkflowToolName);
    }

    private static async Task<IAgentTool> DiscoverScheduleMemberWorkflowToolAsync(
        IStudioMemberWorkflowSchedulePort schedulePort,
        ILogger<ScheduleStudioMemberWorkflowTool>? logger = null)
    {
        if (logger is not null)
            return new ScheduleStudioMemberWorkflowTool(schedulePort, logger);

        var source = new ScheduleStudioMemberWorkflowToolSource(schedulePort);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == ScheduleMemberWorkflowToolName);
    }

    private static async Task<ToolExecutionResult> ExecuteToolThroughExecutorAsync(
        IAgentTool tool,
        string toolCallId,
        string toolName,
        string argumentsJson)
    {
        var tools = new ToolManager();
        tools.Register(tool);
        var executor = new StreamingToolExecutor(
            tools,
            toolExecutionPort: CreateToolExecutionPort());
        using var executionState = executor.CreateExecutionState();
        var prepared = await executor.PrepareBatchAsync(
            $"studio-provisioning-test:{toolCallId}",
            round: 0,
            [new ToolCall
            {
                Id = toolCallId,
                Name = toolName,
                ArgumentsJson = argumentsJson,
            }]);
        executor.AddTool(executionState, prepared.Single());

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        return results.Should().ContainSingle().Which;
    }

    private static IAgentToolExecutionPort CreateToolExecutionPort() =>
        new AdmittedAgentToolExecutor(
            new StartingAdmissionLedger(),
            new AppendedAuditTrail(),
            new StableAuditIdentityHasher());

    private static async Task ExecuteScheduleMemberWorkflowAsync(
        RecordingMemberWorkflowSchedulePort schedulePort,
        string scopeId,
        string memberId,
        string scheduleCron,
        string prompt,
        string displayName)
    {
        var tool = await DiscoverScheduleMemberWorkflowToolAsync(schedulePort);
        using var _ = PushContext(
            scopeId,
            ownerSubject: "owner-1",
            accessToken: "access-token-1",
            requestId: "request-shared",
            callId: "call-shared",
            idempotencyKey: "caller-operation-shared",
            nyxIdAuthority: new AgentToolNyxIdAuthorityContext("nyxid", "tenant-alpha", "nyx-user-alpha"));
        var output = await tool.ExecuteAsync(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["member_id"] = memberId,
            ["schedule_cron"] = scheduleCron,
            ["schedule_timezone"] = "Asia/Shanghai",
            ["prompt"] = prompt,
            ["display_name"] = displayName,
        }));
        ErrorCode(output).Should().BeNull();
    }

    private static AgentToolContextScope PushContext(
        string? scopeId,
        string? ownerSubject,
        string? accessToken,
        string? requestId = "request-1",
        string? callId = "call-1",
        string? idempotencyKey = null,
        string? ownerScopeId = null,
        AgentToolNyxIdAuthorityContext? nyxIdAuthority = null,
        string? senderBindingId = "binding-alpha",
        string? senderNyxUserId = null,
        string? senderTenant = null,
        string? channelPlatform = null,
        string? channelSenderId = null,
        string? channelRegistrationScopeId = null,
        AgentToolNyxIdCredentialKind nyxIdCredentialKind = AgentToolNyxIdCredentialKind.SourceReadableUserBearer)
    {
        return AgentToolContextScope.Push(new AgentToolExecutionContext(
            new AgentToolRequestIdentity(requestId, callId, idempotencyKey),
            new AgentToolCredentials(accessToken, "org-token", "sender-token", nyxIdCredentialKind),
            new AgentToolCallerContext(scopeId, ownerSubject, "response-1", ownerScopeId),
            new AgentToolChannelContext(channelPlatform, channelSenderId, channelRegistrationScopeId, null, null),
            new AgentToolSenderBindingContext(senderBindingId, senderNyxUserId, senderTenant),
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal))
        {
            NyxIdAuthority = nyxIdAuthority ?? AgentToolNyxIdAuthorityContext.Empty,
            ExecutionOwner = AgentToolExecutionOwners.HostService(nameof(ProvisionWorkflowScheduleToolTests)),
        });
    }

    private sealed class StartingAdmissionLedger : IAgentToolAdmissionLedger
    {
        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableAuditIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    public static TheoryData<Exception, string, string> ScheduleMemberWorkflowWritePreflightExceptionCases() => new()
    {
        {
            new StudioMemberAutomationProjectionPendingException(23),
            "authorization_catalog_projection_pending",
            "The refreshed authorization catalog is still being projected. Retry this request."
        },
        {
            new StudioMemberAutomationCatalogRefreshUnavailableException(),
            "authorization_catalog_refresh_unavailable",
            "The authorization catalog could not be refreshed. Retry this request."
        },
        {
            new StudioMemberAutomationCatalogRefreshSupersededException(),
            "authorization_catalog_refresh_superseded",
            "A newer authorization catalog refresh superseded this request. Retry this request."
        },
        {
            new StudioMemberAutomationPlanConflictException(
                "authorization_plan_changed",
                "private authorization planner detail"),
            "authorization_plan_changed",
            "The authorization plan changed. Run schedule preflight again before retrying."
        },
    };

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

    private static string? ErrorAuthorizationPlanMismatchReason(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.TryGetProperty("error", out var error)
            && error.TryGetProperty("authorization_plan_mismatch_reason", out var reason)
            ? reason.GetString()
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
                TeamId: "team-alpha",
                BindingStatus: "accepted",
                ObservatoryUrl: "/workflow/observatory",
                StudioUrl: "/scopes/scope-default/teams/team-alpha/members/member-default/workflow");
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

    private sealed record RecordedLogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<RecordedLogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new RecordedLogEntry(logLevel, formatter(state, exception)));
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

    private sealed class RecordingTeamQueryPort : IStudioTeamQueryPort
    {
        public string? LastListScopeId { get; private set; }
        public StudioTeamRosterPageRequest? LastListPage { get; private set; }
        public string? LastGetScopeId { get; private set; }
        public string? LastGetTeamId { get; private set; }
        public int ListCallCount { get; private set; }
        public int GetCallCount { get; private set; }
        public StudioTeamSummaryResponse? GetResult { get; init; } = DefaultTeam();

        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId,
            StudioTeamRosterPageRequest? page = null,
            CancellationToken ct = default)
        {
            ListCallCount++;
            LastListScopeId = scopeId;
            LastListPage = page;
            return Task.FromResult(new StudioTeamRosterResponse(
                scopeId,
                [DefaultTeam() with { ScopeId = scopeId }],
                "next-page"));
        }

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default)
        {
            GetCallCount++;
            LastGetScopeId = scopeId;
            LastGetTeamId = teamId;
            return Task.FromResult(GetResult is null ? null : GetResult with { ScopeId = scopeId, TeamId = teamId });
        }

        private static StudioTeamSummaryResponse DefaultTeam() =>
            new(
                TeamId: "team-alpha",
                ScopeId: "scope-current",
                DisplayName: "Alpha Team",
                Description: "Current caller scope team",
                LifecycleStage: TeamLifecycleStageNames.Active,
                MemberCount: 2,
                CreatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                UpdatedAt: DateTimeOffset.Parse("2026-07-02T00:00:00Z"))
            {
                EntryMemberId = "m-entry",
            };
    }

    private sealed class RecordingMemberProvisioningPort : IStudioMemberProvisioningPort
    {
        public StudioMemberProvisioningRequest? LastRequest { get; private set; }

        public Task<StudioMemberProvisioningResult> CreateAsync(
            StudioMemberProvisioningRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new StudioMemberProvisioningResult(
                Success: true,
                ScopeId: request.ScopeId,
                MemberId: request.MemberId ?? "member-generated",
                DisplayName: request.DisplayName,
                Description: request.Description ?? string.Empty,
                ImplementationKind: request.ImplementationKind,
                LifecycleStage: "created",
                PublishedServiceId: "published-service-1",
                LastBoundRevisionId: null,
                CreatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                UpdatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"))
            {
                TeamId = request.TeamId,
            });
        }
    }

    private sealed class RecordingMemberWorkflowDraftProvisioningPort :
        IStudioMemberWorkflowDraftProvisioningPort
    {
        public StudioMemberWorkflowDraftProvisioningRequest? LastRequest { get; private set; }

        public Task<StudioMemberWorkflowDraftProvisioningResult> SaveAsync(
            StudioMemberWorkflowDraftProvisioningRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new StudioMemberWorkflowDraftProvisioningResult(
                StudioMemberWorkflowDraftStatusNames.SaveAccepted,
                Runnable: false,
                StudioMemberWorkflowDraftStatusNames.NotBound,
                request.ScopeId,
                request.TeamId,
                request.MemberId ?? "member-generated",
                request.WorkflowId ?? "workflow-generated",
                "/studio/workflows/workflow-alpha",
                "command-alpha",
                "dispatch_accepted",
                "actor-alpha",
                "workspace-alpha",
                7,
                DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                new StudioMemberWorkflowDraftReadiness(
                    Readable: true,
                    Stage: "draft_saved",
                    Message: "Draft saved."),
                []));
        }
    }

    private sealed class RecordingMemberQueryPort : IStudioMemberQueryPort
    {
        public string? LastListScopeId { get; private set; }
        public StudioMemberRosterPageRequest? LastListPage { get; private set; }
        public string? LastGetScopeId { get; private set; }
        public string? LastGetMemberId { get; private set; }
        public int ListCallCount { get; private set; }
        public int GetCallCount { get; private set; }
        public StudioMemberDetailResponse? GetResult { get; init; } = DefaultDetail();

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default)
        {
            ListCallCount++;
            LastListScopeId = scopeId;
            LastListPage = page;
            return Task.FromResult(new StudioMemberRosterResponse(
                scopeId,
                [DefaultSummary() with { ScopeId = scopeId, TeamId = page?.TeamId ?? "team-alpha" }],
                "next-members"));
        }

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default)
        {
            GetCallCount++;
            LastGetScopeId = scopeId;
            LastGetMemberId = memberId;
            if (GetResult is null)
                return Task.FromResult<StudioMemberDetailResponse?>(null);

            var summary = GetResult.Summary with { ScopeId = scopeId, MemberId = memberId };
            return Task.FromResult<StudioMemberDetailResponse?>(GetResult with { Summary = summary });
        }

        private static StudioMemberDetailResponse DefaultDetail() =>
            new(
                DefaultSummary(),
                DefaultImplementationRef(),
                new StudioMemberBindingContractResponse(
                    PublishedServiceId: "svc-alpha",
                    RevisionId: "rev-alpha",
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    BoundAt: DateTimeOffset.Parse("2026-07-03T00:00:00Z"),
                    ExpectedActorId: "workflow-actor-alpha"))
            {
                CurrentBindingRun = new StudioMemberBindingRunStatusResponse(
                    BindingRunId: "binding-run-alpha",
                    ScopeId: "scope-current",
                    MemberId: "m-alpha",
                    Status: StudioMemberBindingRunStatusNames.Succeeded,
                    StateVersion: 42,
                    Failure: null,
                    UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"))
                {
                    Result = new StudioMemberBindingRunResultResponse(
                        PublishedServiceId: "svc-alpha",
                        RevisionId: "rev-alpha",
                        ImplementationKind: MemberImplementationKindNames.Workflow,
                        ExpectedActorId: "workflow-actor-alpha"),
                },
            };

        private static StudioMemberSummaryResponse DefaultSummary() =>
            new(
                MemberId: "m-alpha",
                ScopeId: "scope-current",
                DisplayName: "Alpha Member",
                Description: "Current caller scope member",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                LifecycleStage: MemberLifecycleStageNames.BindReady,
                PublishedServiceId: "svc-alpha",
                LastBoundRevisionId: "rev-alpha",
                CreatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                UpdatedAt: DateTimeOffset.Parse("2026-07-02T00:00:00Z"))
            {
                TeamId = "team-alpha",
                ImplementationRef = DefaultImplementationRef(),
            };

        private static StudioMemberImplementationRefResponse DefaultImplementationRef() =>
            new(
                ImplementationKind: MemberImplementationKindNames.Workflow,
                WorkflowId: "wf-alpha",
                WorkflowRevision: "wf-rev-alpha");
    }

    private sealed class RecordingMemberAutomationQueryPort : IStudioMemberAutomationQueryPort
    {
        public string? LastScopeId { get; private set; }
        public string? LastTeamId { get; private set; }
        public string? LastMemberId { get; private set; }
        public string? LastScheduleId { get; private set; }
        public int? LastTake { get; private set; }
        public string? LastCursor { get; private set; }
        public bool? LastIncludeTotalCount { get; private set; }
        public int ListCallCount { get; private set; }
        public int GetCallCount { get; private set; }
        public StudioMemberAutomationView? GetResult { get; init; } = DefaultView();
        public Exception? Failure { get; init; }

        public Task<StudioMemberAutomationListResponse> ListAsync(
            string scopeId,
            string teamId,
            string? memberId,
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            ListCallCount++;
            LastScopeId = scopeId;
            LastTeamId = teamId;
            LastMemberId = memberId;
            LastTake = take;
            LastCursor = cursor;
            LastIncludeTotalCount = includeTotalCount;
            if (Failure is not null)
                throw Failure;

            return Task.FromResult(new StudioMemberAutomationListResponse(
                [DefaultView() with { ScopeId = scopeId, TeamId = teamId, MemberId = memberId ?? "m-alpha" }],
                "next-schedules",
                1));
        }

        public Task<StudioMemberAutomationView?> GetAsync(
            string scopeId,
            string teamId,
            string memberId,
            string scheduleId,
            CancellationToken ct = default)
        {
            GetCallCount++;
            LastScopeId = scopeId;
            LastTeamId = teamId;
            LastMemberId = memberId;
            LastScheduleId = scheduleId;
            if (Failure is not null)
                throw Failure;

            if (GetResult is null)
                return Task.FromResult<StudioMemberAutomationView?>(null);

            return Task.FromResult<StudioMemberAutomationView?>(GetResult with
            {
                ScopeId = scopeId,
                TeamId = teamId,
                MemberId = memberId,
                ScheduleId = scheduleId,
            });
        }

        private static StudioMemberAutomationView DefaultView() =>
            new(
                ScopeId: "scope-current",
                TeamId: "team-alpha",
                MemberId: "m-alpha",
                ScheduleId: "sched-alpha",
                PublishedServiceId: "svc-alpha",
                DisplayName: "Alpha Schedule",
                Prompt: "Daily summary",
                ScheduleCron: "0 9 * * *",
                ScheduleTimezone: "Asia/Shanghai",
                Enabled: true,
                AuthorizationStatus: "active",
                CredentialExpiresAtUtc: DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
                LastAuthorizationErrorCode: string.Empty,
                OperationId: "operation-alpha",
                CredentialGeneration: 3,
                RevocationPending: false,
                NextFireAt: DateTimeOffset.Parse("2026-07-06T01:00:00Z"),
                LastFireAt: DateTimeOffset.Parse("2026-07-05T01:00:00Z"),
                StateVersion: 42)
            {
                CredentialSourceKind = "scheduled_invocation_agent_key",
                UpdatedAt = DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            };
    }

    private sealed class RecordingMemberWorkflowBindingPort : IStudioMemberWorkflowBindingPort
    {
        public StudioMemberWorkflowBindingRequest? LastRequest { get; private set; }

        public Task<StudioMemberWorkflowBindingResult> BindAsync(
            StudioMemberWorkflowBindingRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new StudioMemberWorkflowBindingResult(
                Success: true,
                ScopeId: request.ScopeId,
                MemberId: request.MemberId,
                Operation: StudioMemberWorkflowBindingOperationNames.Bind,
                Status: "accepted",
                BindingRunId: "binding-run-1",
                AckStage: "dispatch_accepted",
                BindingRunRole: "candidate",
                WorkflowId: request.WorkflowId,
                RevisionId: null));
        }
    }

    private sealed class RecordingMemberWorkflowSchedulePort : IStudioMemberWorkflowSchedulePort
    {
        private const string PermissionDigest = "permission-digest-alpha";
        public const string PolicyVersion = "credential-policy-alpha";

        public List<StudioMemberWorkflowScheduleRequest> PreflightRequests { get; } = [];
        public List<StudioMemberWorkflowScheduleRequest> WritePreflightRequests { get; } = [];
        public List<StudioMemberWorkflowScheduleRequest> CreateRequests { get; } = [];
        public StudioMemberWorkflowScheduleRequest? LastRequest =>
            CreateRequests.LastOrDefault() ?? WritePreflightRequests.LastOrDefault() ?? PreflightRequests.LastOrDefault();
        public int CreateCallCount { get; private set; }
        public Exception? WritePreflightException { get; init; }
        public Exception? CreateException { get; init; }
        public StudioMemberWorkflowAuthorizationResult PreflightResult { get; init; } =
            new(
                true,
                new ScheduledInvocationAuthorizationPlan
                {
                    PermissionDigest = PermissionDigest,
                    CredentialPolicy = new ScheduledInvocationCredentialPolicy
                    {
                        PolicyVersion = PolicyVersion,
                    },
                },
                ScheduledInvocationAuthorizationFailureCode.Unspecified,
                string.Empty);

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default)
        {
            PreflightRequests.Add(request);
            return Task.FromResult(PreflightResult);
        }

        public Task<StudioMemberWorkflowAuthorizationResult> PreflightForWriteAsync(
            StudioMemberWorkflowScheduleRequest request,
            CancellationToken ct = default)
        {
            WritePreflightRequests.Add(request);
            PreflightRequests.Add(request);
            if (WritePreflightException is not null)
                return Task.FromException<StudioMemberWorkflowAuthorizationResult>(WritePreflightException);

            return Task.FromResult(PreflightResult);
        }

        public Task<StudioMemberWorkflowScheduleResult> CreateAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default) =>
            CompleteAsync(request, confirmedPermissionDigest);

        public Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest,
            CancellationToken ct = default) =>
            CompleteAsync(request, confirmedPermissionDigest);

        public Task<StudioMemberAutomationListResponse> ListAsync(
            string scopeId,
            string teamId,
            string? memberId,
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationView?> GetAsync(
            string scopeId,
            string teamId,
            string memberId,
            string scheduleId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> UpdateAsync(
            StudioMemberAutomationUpdateCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> PauseAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> ResumeAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> RunNowAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberAutomationMutationReceipt> DeleteAsync(
            StudioMemberAutomationActionCommand command,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        private Task<StudioMemberWorkflowScheduleResult> CompleteAsync(
            StudioMemberWorkflowScheduleRequest request,
            string confirmedPermissionDigest)
        {
            CreateCallCount++;
            if (CreateException is not null)
                return Task.FromException<StudioMemberWorkflowScheduleResult>(CreateException);

            if (!string.Equals(confirmedPermissionDigest, PermissionDigest, StringComparison.Ordinal))
                throw new InvalidOperationException("authorization_plan_changed");
            if (string.IsNullOrWhiteSpace(request.OperationId))
                throw new InvalidOperationException("operation_id_required");
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                throw new InvalidOperationException("idempotency_key_required");
            if (!string.Equals(
                    request.CredentialProvisioningKind,
                    "dedicated_scheduled_invocation_agent_key",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("credential_provisioning_kind_invalid");
            }
            if (!string.Equals(request.ConfirmedPolicyVersion, PolicyVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("confirmed_policy_version_invalid");

            var preflightRequest = PreflightRequests.LastOrDefault()
                ?? throw new InvalidOperationException("preflight_required");
            if (!string.Equals(preflightRequest.OperationId, request.OperationId, StringComparison.Ordinal) ||
                !string.Equals(preflightRequest.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("preflight_operation_identity_changed");
            }

            CreateRequests.Add(request);
            return Task.FromResult(new StudioMemberWorkflowScheduleResult(
                Success: true,
                ScopeId: request.ScopeId,
                MemberId: request.MemberId,
                ScheduleId: "schedule-member-1",
                PublishedServiceId: "published-member-1",
                ObservatoryUrl: "/workflow/observatory",
                Status: "accepted"));
        }
    }
}
