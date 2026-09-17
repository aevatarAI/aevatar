using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberScopeWorkflowDescriptorSourceTests
{
    [Fact]
    public async Task FindByWorkflowIdAsync_ShouldMapDistinctMemberWorkflowAndPublishedServiceIdentities()
    {
        var member = CreateWorkflowMember("m-alpha", "wf-alpha", "svc-alpha");
        var source = new StudioMemberScopeWorkflowDescriptorSource(new FixedMemberQueryPort(member));

        var result = await source.FindByWorkflowIdAsync("scope-alpha", "wf-alpha");

        result.Should().ContainSingle();
        result[0].WorkflowId.Should().Be("wf-alpha");
        result[0].PublishedServiceId.Should().Be("svc-alpha");
        result[0].ServiceAppId.Should().Be("studio");
        result[0].WorkflowId.Should().NotBe(member.MemberId);
        result[0].PublishedServiceId.Should().NotBe(member.MemberId);
    }

    [Fact]
    public async Task FindByWorkflowIdAsync_ShouldNotTreatMemberIdAsWorkflowId()
    {
        var member = CreateWorkflowMember("m-alpha", "wf-alpha", "svc-alpha");
        var source = new StudioMemberScopeWorkflowDescriptorSource(new FixedMemberQueryPort(member));

        var result = await source.FindByWorkflowIdAsync("scope-alpha", "m-alpha");

        result.Should().BeEmpty();
    }

    private static StudioMemberSummaryResponse CreateWorkflowMember(
        string memberId,
        string workflowId,
        string publishedServiceId) =>
        new(
            MemberId: memberId,
            ScopeId: "scope-alpha",
            DisplayName: "Case 19",
            Description: string.Empty,
            ImplementationKind: MemberImplementationKindNames.Workflow,
            LifecycleStage: MemberLifecycleStageNames.BindReady,
            PublishedServiceId: publishedServiceId,
            LastBoundRevisionId: "rev-alpha",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow)
        {
            ImplementationRef = new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.Workflow,
                WorkflowId: workflowId,
                WorkflowRevision: "rev-alpha"),
        };

    private sealed class FixedMemberQueryPort : IStudioMemberQueryPort
    {
        private readonly IReadOnlyList<StudioMemberSummaryResponse> _members;

        public FixedMemberQueryPort(params StudioMemberSummaryResponse[] members)
        {
            _members = members;
        }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(
                scopeId,
                _members.Where(member => member.ScopeId == scopeId).ToArray()));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            Task.FromResult<StudioMemberDetailResponse?>(null);
    }
}
