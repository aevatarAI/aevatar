using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Tests.Shared;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberWorkflowDraftProvisioningServiceTests
{
    private const string ScopeId = "scope-alpha";
    private const string TeamId = "team-alpha";
    private const string MemberId = "wf-51bf3cacce0f66698706533011c28f7a";
    private const string WorkflowId = "workflow-51bf3cacce0f66698706533011c28f7a";

    private const string UnresolvedYaml = """
        name: x_digest
        steps:
          - id: fetch
            type: tool_call
            parameters:
              tool: nyxid_proxy
              arguments: '{"query":{"request":"${input}"}}'
        """;

    [Fact]
    public async Task SaveAsync_WithUnresolvedNyxIdCall_ShouldCreateMemberShellAndAcceptDraftOnly()
    {
        var parser = new StubWorkflowDefinitionParser(UnresolvedParseResult());
        var members = new RecordingMemberPorts();
        var workspace = new RecordingStudioWorkspacePorts();
        var service = NewService(parser, members, workspace, workspace);

        var result = await service.SaveAsync(new StudioMemberWorkflowDraftProvisioningRequest(
            ScopeId,
            TeamId,
            "X Digest",
            UnresolvedYaml));

        var create = members.CreateRequests.Should().ContainSingle().Subject;
        create.MemberId.Should().Be(MemberId);
        create.TeamId.Should().Be(TeamId);
        create.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        var saved = workspace.SavedDrafts.Should().ContainSingle().Subject;
        saved.WorkflowId.Should().Be(WorkflowId);
        saved.Yaml.Should().Be(UnresolvedYaml.Trim());

        result.Status.Should().Be("draft_save_accepted");
        result.Runnable.Should().BeFalse();
        result.BindingStatus.Should().Be("not_bound");
        result.MemberId.Should().Be(MemberId);
        result.WorkflowId.Should().Be(WorkflowId);
        result.MemberId.Should().NotBe(result.WorkflowId);
        result.StudioUrl.Should().Be(
            $"/scopes/{ScopeId}/teams/{TeamId}/members/{MemberId}/workflow?workflowId={WorkflowId}");
        result.AckStage.Should().Be("accepted");
        result.Readiness.Readable.Should().BeFalse();
        result.Readiness.Stage.Should().Be("projection_pending");
        result.Blockers.Should().ContainSingle().Which.Code
            .Should().Be("NYXID_OPERATION_SELECTION_REQUIRED");
    }

    [Fact]
    public async Task SaveAsync_WithSameOwnershipTuple_ShouldReuseDeterministicIdentities()
    {
        var parser = new StubWorkflowDefinitionParser(UnresolvedParseResult());
        var members = new RecordingMemberPorts();
        var workspace = new RecordingStudioWorkspacePorts();
        var service = NewService(parser, members, workspace, workspace);
        var request = new StudioMemberWorkflowDraftProvisioningRequest(
            ScopeId,
            TeamId,
            "X Digest",
            UnresolvedYaml);

        var first = await service.SaveAsync(request);
        var second = await service.SaveAsync(request);

        first.MemberId.Should().Be(MemberId);
        second.MemberId.Should().Be(MemberId);
        first.WorkflowId.Should().Be(WorkflowId);
        second.WorkflowId.Should().Be(WorkflowId);
        members.CreateRequests.Should().HaveCount(2)
            .And.OnlyContain(item => item.MemberId == MemberId);
        workspace.SavedDrafts.Should().HaveCount(2)
            .And.OnlyContain(item => item.WorkflowId == WorkflowId);
    }

    [Fact]
    public async Task SaveAsync_WithSameWorkflowNameAcrossTeams_ShouldUseDistinctDraftPaths()
    {
        var parser = new StubWorkflowDefinitionParser(UnresolvedParseResult());
        var members = new RecordingMemberPorts();
        var workspace = new RecordingStudioWorkspacePorts();
        var service = NewService(parser, members, workspace, workspace);

        var first = await service.SaveAsync(new StudioMemberWorkflowDraftProvisioningRequest(
            ScopeId,
            TeamId,
            "X Digest",
            UnresolvedYaml));
        var second = await service.SaveAsync(new StudioMemberWorkflowDraftProvisioningRequest(
            ScopeId,
            "team-beta",
            "X Digest",
            UnresolvedYaml));

        first.WorkflowId.Should().Be(WorkflowId);
        second.WorkflowId.Should().Be("workflow-df0d5cf4c0f1d4ab5bb2752366282b04");
        var drafts = (await workspace.GetAsync(ScopeId)).Drafts;
        drafts.Should().ContainSingle(draft =>
            draft.WorkflowId == first.WorkflowId &&
            draft.FileName == $"{first.WorkflowId}.yaml");
        drafts.Should().ContainSingle(draft =>
            draft.WorkflowId == second.WorkflowId &&
            draft.FileName == $"{second.WorkflowId}.yaml");
    }

    [Fact]
    public async Task SaveAsync_WithExactNyxIdSelector_ShouldRemainUnboundAndNotRunnable()
    {
        var parser = new StubWorkflowDefinitionParser(ExactParseResult());
        var members = new RecordingMemberPorts();
        var workspace = new RecordingStudioWorkspacePorts();
        var service = NewService(parser, members, workspace, workspace);

        var result = await service.SaveAsync(new StudioMemberWorkflowDraftProvisioningRequest(
            ScopeId,
            TeamId,
            "X Digest",
            UnresolvedYaml));

        result.Runnable.Should().BeFalse();
        result.BindingStatus.Should().Be("not_bound");
        result.Blockers.Should().ContainSingle().Which.Code
            .Should().Be("WORKFLOW_BIND_REQUIRED");
    }

    [Fact]
    public async Task SaveAsync_WhenYamlInvalid_ShouldMutateNothing()
    {
        var parser = new StubWorkflowDefinitionParser(WorkflowYamlParseResult.Invalid("invalid yaml"));
        var members = new RecordingMemberPorts();
        var workspace = new RecordingStudioWorkspacePorts();
        var service = NewService(parser, members, workspace, workspace);

        var action = () => service.SaveAsync(new StudioMemberWorkflowDraftProvisioningRequest(
            ScopeId,
            TeamId,
            "X Digest",
            UnresolvedYaml));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("invalid yaml");
        members.CreateRequests.Should().BeEmpty();
        workspace.SavedDrafts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("other-team", MemberImplementationKindNames.Workflow, "member_team_mismatch")]
    [InlineData(TeamId, MemberImplementationKindNames.Script, "member_kind_mismatch")]
    public async Task SaveAsync_WithInvalidExistingMember_ShouldRejectBeforeDraftWrite(
        string existingTeamId,
        string implementationKind,
        string expectedCode)
    {
        var parser = new StubWorkflowDefinitionParser(UnresolvedParseResult());
        var members = new RecordingMemberPorts
        {
            ExistingMember = NewMemberDetail("m-alpha", existingTeamId, implementationKind),
        };
        var workspace = new RecordingStudioWorkspacePorts();
        var service = NewService(parser, members, workspace, workspace);
        var request = new StudioMemberWorkflowDraftProvisioningRequest(
            ScopeId,
            TeamId,
            "X Digest",
            UnresolvedYaml)
        {
            MemberId = "m-alpha",
            WorkflowId = "wf-alpha",
        };

        var exception = await FluentActions.Invoking(() => service.SaveAsync(request))
            .Should().ThrowAsync<StudioMemberWorkflowDraftProvisioningException>();

        exception.Which.Code.Should().Be(expectedCode);
        exception.Which.MemberId.Should().Be("m-alpha");
        members.CreateRequests.Should().BeEmpty();
        workspace.SavedDrafts.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_WhenWorkspaceSaveFails_ShouldReturnReusableMemberIdentity()
    {
        var parser = new StubWorkflowDefinitionParser(UnresolvedParseResult());
        var members = new RecordingMemberPorts();
        var query = new RecordingStudioWorkspacePorts();
        var service = NewService(
            parser,
            members,
            query,
            new FailingWorkspaceCommandPort(new InvalidOperationException("workspace unavailable")));

        var exception = await FluentActions.Invoking(() => service.SaveAsync(
                new StudioMemberWorkflowDraftProvisioningRequest(
                    ScopeId,
                    TeamId,
                    "X Digest",
                    UnresolvedYaml)))
            .Should().ThrowAsync<StudioMemberWorkflowDraftProvisioningException>();

        exception.Which.Code.Should().Be("workflow_draft_save_failed");
        exception.Which.MemberId.Should().Be(MemberId);
        exception.Which.Message.Should().NotContain("workspace unavailable");
    }

    private static StudioMemberWorkflowDraftProvisioningService NewService(
        IWorkflowDefinitionParser parser,
        RecordingMemberPorts members,
        IStudioWorkspaceQueryPort workspaceQuery,
        IStudioWorkspaceCommandPort workspaceCommand) =>
        new(
            members,
            members,
            parser,
            new AppScopedWorkflowService(
                new StubWorkflowYamlDocumentService(),
                parser,
                workspaceQuery,
                workspaceCommand));

    private static WorkflowYamlParseResult UnresolvedParseResult()
    {
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "x_digest/fetch",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector(),
        });
        return WorkflowYamlParseResult.Success("x_digest", dependencies);
    }

    private static WorkflowYamlParseResult ExactParseResult()
    {
        var dependencies = new WorkflowAuthorizationDependencies
        {
            ServiceGrantPolicy = WorkflowServiceGrantPolicy.Required,
        };
        dependencies.ExternalInvocations.Add(new ExternalToolInvocationSpec
        {
            CallSiteId = "x_digest/fetch",
            ToolName = "nyxid_proxy",
            Selector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = "us-x-alpha",
                    EndpointId = "list-following",
                },
            },
        });
        return WorkflowYamlParseResult.Success("x_digest", dependencies);
    }

    private static StudioMemberDetailResponse NewMemberDetail(
        string memberId,
        string teamId,
        string implementationKind)
    {
        var summary = new StudioMemberSummaryResponse(
            memberId,
            ScopeId,
            "Existing member",
            string.Empty,
            implementationKind,
            MemberLifecycleStageNames.Created,
            "svc-alpha",
            null,
            DateTimeOffset.Parse("2026-07-30T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-30T00:00:00Z"))
        {
            TeamId = teamId,
        };
        return new StudioMemberDetailResponse(summary, null, null);
    }

    private sealed class RecordingMemberPorts : IStudioMemberProvisioningPort, IStudioMemberQueryPort
    {
        public List<StudioMemberProvisioningRequest> CreateRequests { get; } = [];
        public StudioMemberDetailResponse? ExistingMember { get; init; }

        public Task<StudioMemberProvisioningResult> CreateAsync(
            StudioMemberProvisioningRequest request,
            CancellationToken ct = default)
        {
            CreateRequests.Add(request);
            return Task.FromResult(new StudioMemberProvisioningResult(
                true,
                request.ScopeId,
                request.MemberId!,
                request.DisplayName,
                request.Description ?? string.Empty,
                request.ImplementationKind,
                MemberLifecycleStageNames.Created,
                "svc-alpha",
                null,
                DateTimeOffset.Parse("2026-07-30T00:00:00Z"),
                DateTimeOffset.Parse("2026-07-30T00:00:00Z"))
            {
                TeamId = request.TeamId,
            });
        }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, []));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            Task.FromResult(ExistingMember is { } member && member.Summary.MemberId == memberId
                ? member
                : null);
    }

    private sealed class StubWorkflowDefinitionParser(WorkflowYamlParseResult result) : IWorkflowDefinitionParser
    {
        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default) =>
            Task.FromResult(result);

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        public WorkflowParseResult Parse(string yaml) =>
            new(new WorkflowDocument { Name = "x_digest" }, []);

        public string Serialize(WorkflowDocument document) =>
            throw new NotSupportedException();
    }

    private sealed class FailingWorkspaceCommandPort(Exception failure) : IStudioWorkspaceCommandPort
    {
        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
            StudioWorkflowDraftRecord draft,
            long? expectedVersion = null,
            CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(failure);

        public Task<StudioWorkspaceCommandReceipt> SaveDraftAsync(
            string scopeId,
            StudioWorkflowDraftRecord draft,
            long? expectedVersion = null,
            CancellationToken ct = default) =>
            Task.FromException<StudioWorkspaceCommandReceipt>(failure);

        public Task<StudioWorkspaceCommandReceipt> UpdateSettingsAsync(
            StudioWorkspaceSettings settings,
            long? expectedVersion = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioWorkspaceCommandReceipt> AddDirectoryAsync(
            StudioWorkspaceDirectory directory,
            long? expectedVersion = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioWorkspaceCommandReceipt> RemoveDirectoryAsync(
            string directoryId,
            long? expectedVersion = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
            string workflowId,
            long? expectedVersion = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StudioWorkspaceCommandReceipt> DeleteDraftAsync(
            string scopeId,
            string workflowId,
            long? expectedVersion = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
