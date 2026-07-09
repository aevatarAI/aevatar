using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberWorkflowBindingPortTests
{
    [Fact]
    public async Task BindAsync_WhenWorkflowIdMissing_ShouldDeriveStableWorkflowId()
    {
        var memberService = new RecordingMemberService();
        var parser = new RecordingWorkflowDefinitionParser();
        var port = new StudioMemberWorkflowBindingPort(memberService, parser);

        await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n"));

        memberService.LastScopeId.Should().Be("scope-1");
        memberService.LastMemberId.Should().Be("member-1");
        memberService.LastRequest.Should().NotBeNull();
        memberService.LastRequest!.Workflow.Should().NotBeNull();
        memberService.LastRequest.Workflow!.WorkflowId.Should().StartWith("workflow-");
        memberService.LastRequest.Workflow.WorkflowId.Should().NotBe("workflow-member-1");
        memberService.LastRequest.Workflow.WorkflowId.Should().HaveLength("workflow-".Length + 32);
        memberService.LastRequest.Workflow.WorkflowYamls.Should().ContainSingle()
            .Which.Should().Contain("name: demo");
    }

    [Fact]
    public async Task BindAsync_WhenWorkflowIdMissing_ShouldConvergePerScopeAndMember()
    {
        var first = await BindWithoutWorkflowIdAsync("scope-1", "member-1");
        var second = await BindWithoutWorkflowIdAsync("scope-1", "member-1");
        var differentScope = await BindWithoutWorkflowIdAsync("scope-2", "member-1");

        first.Should().Be(second);
        differentScope.Should().NotBe(first);
    }

    [Fact]
    public async Task BindAsync_WhenWorkflowIdProvided_ShouldUseTrimmedWorkflowId()
    {
        var memberService = new RecordingMemberService();
        var parser = new RecordingWorkflowDefinitionParser();
        var port = new StudioMemberWorkflowBindingPort(memberService, parser);

        await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "name: demo\nsteps: []\n")
        {
            WorkflowId = " workflow-explicit ",
        });

        memberService.LastRequest.Should().NotBeNull();
        memberService.LastRequest!.Workflow.Should().NotBeNull();
        memberService.LastRequest.Workflow!.WorkflowId.Should().Be("workflow-explicit");
    }

    [Fact]
    public async Task BindAsync_WhenWorkflowYamlInvalid_ShouldRejectBeforeDispatchingBind()
    {
        var memberService = new RecordingMemberService();
        var parser = new RecordingWorkflowDefinitionParser
        {
            Error = "missing workflow name",
        };
        var port = new StudioMemberWorkflowBindingPort(memberService, parser);

        var action = () => port.BindAsync(new StudioMemberWorkflowBindingRequest(
            ScopeId: "scope-1",
            MemberId: "member-1",
            WorkflowYaml: "steps: []\n"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("workflow_yaml is not a valid workflow definition: missing workflow name");
        parser.LastYaml.Should().Be("steps: []\n");
        memberService.LastRequest.Should().BeNull();
    }

    private static async Task<string> BindWithoutWorkflowIdAsync(string scopeId, string memberId)
    {
        var memberService = new RecordingMemberService();
        var parser = new RecordingWorkflowDefinitionParser();
        var port = new StudioMemberWorkflowBindingPort(memberService, parser);

        await port.BindAsync(new StudioMemberWorkflowBindingRequest(
            scopeId,
            memberId,
            "name: demo\nsteps: []\n"));

        return memberService.LastRequest!.Workflow!.WorkflowId;
    }

    private sealed class RecordingWorkflowDefinitionParser : IWorkflowDefinitionParser
    {
        public string? Error { get; init; }
        public string? LastYaml { get; private set; }

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            LastYaml = workflowYaml;
            return Task.FromResult(Error is null
                ? WorkflowYamlParseResult.Success("demo")
                : WorkflowYamlParseResult.Invalid(Error));
        }
    }

    private sealed class RecordingMemberService : IStudioMemberService
    {
        public string? LastScopeId { get; private set; }
        public string? LastMemberId { get; private set; }
        public UpdateStudioMemberBindingRequest? LastRequest { get; private set; }

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId,
            string memberId,
            UpdateStudioMemberBindingRequest request,
            CancellationToken ct = default)
        {
            LastScopeId = scopeId;
            LastMemberId = memberId;
            LastRequest = request;
            return Task.FromResult(new StudioMemberBindingAcceptedResponse(
                Status: StudioMemberBindingRunStatusNames.Accepted,
                BindingRunId: "bind-run-1",
                ScopeId: scopeId,
                MemberId: memberId));
        }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId,
            CreateStudioMemberRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId,
            string memberId,
            string endpointId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId,
            string memberId,
            string revisionId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId,
            string memberId,
            string revisionId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberCommandResponse> UpdateAsync(
            string scopeId,
            string memberId,
            UpdateStudioMemberRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StudioMemberCommandResponse> DeleteAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
