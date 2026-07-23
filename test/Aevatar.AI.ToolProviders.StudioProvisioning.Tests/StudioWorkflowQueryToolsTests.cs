using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.StudioProvisioning.Tests;

public sealed class StudioWorkflowQueryToolsTests
{
    private const string ListWorkflowsToolName = "aevatar_list_workflows";

    [Fact]
    public async Task Source_WithMemberQueryPort_ShouldDiscoverWorkspaceWorkflowTool()
    {
        var source = new StudioWorkflowQueryToolSource(new RecordingMemberQueryPort());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be(ListWorkflowsToolName);
        tools[0].IsReadOnly.Should().BeTrue();
        tools[0].IsDestructive.Should().BeFalse();
        tools[0].Description.Should().Contain("current Aevatar workspace");
        tools[0].Description.Should().Contain("Team-owned workflow members");
    }

    [Fact]
    public async Task Source_WithoutMemberQueryPort_ShouldDiscoverNoTools()
    {
        var source = new StudioWorkflowQueryToolSource();

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public void AddStudioProvisioningTools_ShouldRegisterWorkspaceWorkflowSource()
    {
        var services = new ServiceCollection();

        services.AddStudioProvisioningTools();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IAgentToolSource)
            && descriptor.ImplementationType == typeof(StudioWorkflowQueryToolSource));
    }

    [Fact]
    public async Task ListWorkflows_ShouldUseCallerScopeAndReturnOnlyTeamOwnedWorkflowMembers()
    {
        var port = new RecordingMemberQueryPort
        {
            Result = new StudioMemberRosterResponse(
                "scope-current",
                [
                    WorkflowMember(
                        scopeId: "scope-current",
                        teamId: "team-alpha",
                        memberId: "m-alpha",
                        workflowId: "wf-alpha",
                        publishedServiceId: "svc-alpha"),
                    WorkflowMember(
                        scopeId: "scope-current",
                        teamId: "team-alpha",
                        memberId: "m-unbound",
                        workflowId: null,
                        publishedServiceId: "svc-unbound"),
                    ScriptMember(),
                    WorkflowMember(
                        scopeId: "scope-current",
                        teamId: null,
                        memberId: "m-unassigned",
                        workflowId: "wf-unassigned",
                        publishedServiceId: "svc-unassigned"),
                    WorkflowMember(
                        scopeId: "scope-other",
                        teamId: "team-other",
                        memberId: "m-other",
                        workflowId: "wf-other",
                        publishedServiceId: "svc-other"),
                ],
                "next-page"),
        };
        var tool = await DiscoverToolAsync(port);
        using var callerCancellation = new CancellationTokenSource();
        var callerToken = callerCancellation.Token;
        using var _ = PushContext(scopeId: "scope-current");

        var output = await tool.ExecuteAsync(
            """{"team_id":"team-alpha","page_size":20,"page_token":"page-2"}""",
            callerToken);

        port.LastScopeId.Should().Be("scope-current");
        port.LastPage.Should().Be(new StudioMemberRosterPageRequest(20, "page-2", "team-alpha"));
        port.LastCancellationToken.Should().Be(callerToken);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        PropertyNames(root).Should().Equal("scope_id", "workflows", "next_page_token");
        root.GetProperty("scope_id").GetString().Should().Be("scope-current");
        root.GetProperty("next_page_token").GetString().Should().Be("next-page");

        var workflows = root.GetProperty("workflows");
        workflows.GetArrayLength().Should().Be(2);
        var bound = workflows[0];
        bound.GetProperty("scope_id").GetString().Should().Be("scope-current");
        bound.GetProperty("team_id").GetString().Should().Be("team-alpha");
        bound.GetProperty("member_id").GetString().Should().Be("m-alpha");
        bound.GetProperty("workflow_id").GetString().Should().Be("wf-alpha");
        bound.GetProperty("published_service_id").GetString().Should().Be("svc-alpha");
        bound.GetProperty("workflow_url").GetString().Should().Be(
            "/scopes/scope-current/teams/team-alpha/members/m-alpha/workflow");

        var unbound = workflows[1];
        unbound.GetProperty("member_id").GetString().Should().Be("m-unbound");
        unbound.TryGetProperty("workflow_id", out var workflowId).Should().BeTrue();
        workflowId.ValueKind.Should().Be(JsonValueKind.Null);

        output.Should().NotContain("m-script");
        output.Should().NotContain("m-unassigned");
        output.Should().NotContain("m-other");
    }

    [Fact]
    public async Task ListWorkflows_WhenOwnerScopeExists_ShouldPreferOwnerScope()
    {
        var port = new RecordingMemberQueryPort
        {
            Result = new StudioMemberRosterResponse("owner-scope", []),
        };
        var tool = await DiscoverToolAsync(port);
        using var _ = PushContext(scopeId: "registration-scope", ownerScopeId: "owner-scope");

        await tool.ExecuteAsync("{}");

        port.LastScopeId.Should().Be("owner-scope");
        port.LastPage.Should().BeNull();
    }

    [Fact]
    public async Task ListWorkflows_WhenScopeMissing_ShouldReturnStructuredErrorWithoutQuery()
    {
        var port = new RecordingMemberQueryPort();
        var tool = await DiscoverToolAsync(port);
        using var _ = PushContext(scopeId: null);

        var output = await tool.ExecuteAsync("{}");

        AssertError(output, "caller_scope_unavailable");
        port.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ListWorkflows_WhenModelSuppliesScope_ShouldRejectUnknownArgumentWithoutQuery()
    {
        var port = new RecordingMemberQueryPort();
        var tool = await DiscoverToolAsync(port);
        using var _ = PushContext(scopeId: "scope-current");

        var output = await tool.ExecuteAsync("""{"scope_id":"scope-model"}""");

        AssertError(output, "invalid_arguments", "scope_id");
        port.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ListWorkflows_WhenJsonIsMalformed_ShouldReturnInvalidArgumentsWithoutQuery()
    {
        var port = new RecordingMemberQueryPort();
        var tool = await DiscoverToolAsync(port);
        using var _ = PushContext(scopeId: "scope-current");

        var output = await tool.ExecuteAsync("{");

        AssertError(output, "invalid_arguments", "Could not parse tool arguments");
        port.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ListWorkflows_WhenCanceled_ShouldRethrowCancellation()
    {
        var port = new RecordingMemberQueryPort
        {
            Failure = new OperationCanceledException("member query canceled"),
        };
        var tool = await DiscoverToolAsync(port);
        using var _ = PushContext(scopeId: "scope-current");

        var act = () => tool.ExecuteAsync("{}");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ListWorkflows_WhenProviderFails_ShouldReturnSafeStructuredError()
    {
        var port = new RecordingMemberQueryPort
        {
            Failure = new InvalidOperationException("sensitive member query detail"),
        };
        var tool = await DiscoverToolAsync(port);
        using var _ = PushContext(scopeId: "scope-current");

        var output = await tool.ExecuteAsync("{}");

        AssertError(output, "workflow_query_failed", nameof(InvalidOperationException));
        output.Should().NotContain("sensitive member query detail");
    }

    private static async Task<IAgentTool> DiscoverToolAsync(IStudioMemberQueryPort port)
    {
        var source = new StudioWorkflowQueryToolSource(port);
        var tools = await source.DiscoverToolsAsync();
        return tools.Single(tool => tool.Name == ListWorkflowsToolName);
    }

    private static AgentToolContextScope PushContext(string? scopeId, string? ownerScopeId = null) =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext(scopeId, "owner-alpha", "response-alpha", ownerScopeId),
        });

    private static StudioMemberSummaryResponse WorkflowMember(
        string scopeId,
        string? teamId,
        string memberId,
        string? workflowId,
        string publishedServiceId) =>
        new(
            MemberId: memberId,
            ScopeId: scopeId,
            DisplayName: $"Workflow {memberId}",
            Description: "Team-owned workflow member",
            ImplementationKind: MemberImplementationKindNames.Workflow,
            LifecycleStage: MemberLifecycleStageNames.BindReady,
            PublishedServiceId: publishedServiceId,
            LastBoundRevisionId: workflowId is null ? null : $"rev-{memberId}",
            CreatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-02T00:00:00Z"))
        {
            TeamId = teamId,
            ImplementationRef = workflowId is null
                ? null
                : new StudioMemberImplementationRefResponse(
                    MemberImplementationKindNames.Workflow,
                    WorkflowId: workflowId,
                    WorkflowRevision: $"workflow-rev-{memberId}"),
        };

    private static StudioMemberSummaryResponse ScriptMember() =>
        new(
            MemberId: "m-script",
            ScopeId: "scope-current",
            DisplayName: "Script member",
            Description: "Not a workflow",
            ImplementationKind: MemberImplementationKindNames.Script,
            LifecycleStage: MemberLifecycleStageNames.BindReady,
            PublishedServiceId: "svc-script",
            LastBoundRevisionId: "rev-script",
            CreatedAt: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-02T00:00:00Z"))
        {
            TeamId = "team-alpha",
            ImplementationRef = new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.Script,
                ScriptId: "script-alpha",
                ScriptRevision: "script-rev-alpha"),
        };

    private static IEnumerable<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    private static void AssertError(string output, string code, string? messageFragment = null)
    {
        using var document = JsonDocument.Parse(output);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(code);
        if (messageFragment is not null)
            error.GetProperty("message").GetString().Should().Contain(messageFragment);
    }

    private sealed class RecordingMemberQueryPort : IStudioMemberQueryPort
    {
        public StudioMemberRosterResponse Result { get; init; } =
            new("scope-current", []);

        public Exception? Failure { get; init; }

        public int CallCount { get; private set; }

        public string? LastScopeId { get; private set; }

        public StudioMemberRosterPageRequest? LastPage { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default)
        {
            CallCount++;
            LastScopeId = scopeId;
            LastPage = page;
            LastCancellationToken = ct;
            return Failure is null
                ? Task.FromResult(Result)
                : Task.FromException<StudioMemberRosterResponse>(Failure);
        }

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
