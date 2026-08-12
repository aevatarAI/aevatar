using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Tests.Shared;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class AppScopedWorkflowCatalogueServiceTests
{
    private const string ScopeId = "scope-alpha";

    [Fact]
    public async Task QueryAsync_ShouldMergeDraftOnlyCommittedOnlyAndOverlappingRowsByWorkflowId()
    {
        var service = CreateService(
            [
                Draft("wf-alpha", "Draft Alpha", "draft alpha description", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
                Draft("wf-overlap", "Draft Overlap", "draft overlap description", DateTimeOffset.Parse("2026-08-03T00:00:00Z")),
            ],
            [
                Committed("wf-beta", "Committed Beta", "m-beta", "svc-beta", DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
                Committed("wf-overlap", "Committed Overlap", "m-overlap", "svc-overlap", DateTimeOffset.Parse("2026-08-04T00:00:00Z")),
            ]);

        var response = await service.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId));

        response.Items.Select(static item => item.WorkflowId).Should().Equal("wf-overlap", "wf-beta", "wf-alpha");
        var overlap = response.Items[0];
        overlap.HasDraftSource.Should().BeTrue();
        overlap.HasCommittedSource.Should().BeTrue();
        overlap.Name.Should().Be("Draft Overlap");
        overlap.Description.Should().Be("draft overlap description");
        overlap.UpdatedAtSource.Should().Be("committed");
        overlap.Capabilities.Open.Available.Should().BeTrue();
        overlap.Capabilities.Activity.Available.Should().BeTrue();
        overlap.Capabilities.Rename.Available.Should().BeTrue();
        overlap.Capabilities.Delete.Available.Should().BeTrue();
        overlap.Committed.Should().NotBeNull();
        overlap.Committed!.ActorId.Should().Be("m-overlap");
        overlap.Committed.DeploymentId.Should().Be("svc-overlap");
        response.Freshness.RefreshWatermarkUtc.Should().Be(DateTimeOffset.Parse("2026-08-04T00:00:00Z"));
    }

    [Fact]
    public async Task QueryAsync_WithDraftView_ShouldReturnDraftSubsetWithCommittedFacts()
    {
        var service = CreateService(
            [
                Draft("wf-alpha", "Draft Alpha", "draft alpha description", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
                Draft("wf-overlap", "Draft Overlap", "draft overlap description", DateTimeOffset.Parse("2026-08-03T00:00:00Z")),
            ],
            [
                Committed("wf-beta", "Committed Beta", "m-beta", "svc-beta", DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
                Committed("wf-overlap", "Committed Overlap", "m-overlap", "svc-overlap", DateTimeOffset.Parse("2026-08-04T00:00:00Z")),
            ]);

        var response = await service.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            ScopeWorkflowCatalogueView.Drafts));

        response.Items.Select(static item => item.WorkflowId).Should().Equal("wf-overlap", "wf-alpha");
        response.Items[0].HasCommittedSource.Should().BeTrue();
        response.Items[0].Capabilities.Activity.Available.Should().BeTrue();
    }

    [Fact]
    public async Task QueryAsync_ShouldSearchNameDescriptionChineseTextAndWorkflowIdPrefix()
    {
        var service = CreateService(
            [
                Draft("wf-alpha", "Alpha Draft", "Handles 审批 flow", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
                Draft("wf-gamma", "Gamma Draft", "Other", DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
            ],
            [
                Committed("wf-beta", "Billing Review", "m-beta", "svc-beta", DateTimeOffset.Parse("2026-08-03T00:00:00Z")),
            ]);

        (await service.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId, Query: "alpha")))
            .Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-alpha");
        (await service.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId, Query: "审批")))
            .Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-alpha");
        (await service.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId, Query: "WF-B")))
            .Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-beta");
        (await service.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId, Query: "missing")))
            .Items.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_ShouldSearchBeforeCursorPagination()
    {
        var service = CreateService(
            [
                Draft("wf-alpha", "Searchable Alpha", "same", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
                Draft("wf-beta", "Searchable Beta", "same", DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
                Draft("wf-gamma", "Other", "same", DateTimeOffset.Parse("2026-08-03T00:00:00Z")),
            ],
            []);

        var firstPage = await service.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            Query: "searchable",
            Take: 1));
        var secondPage = await service.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            Query: "searchable",
            Cursor: firstPage.NextPageToken,
            Take: 1));

        firstPage.Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-beta");
        firstPage.NextPageToken.Should().Be("1");
        secondPage.Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-alpha");
        secondPage.NextPageToken.Should().BeNull();
    }

    [Fact]
    public async Task QueryAsync_WithDraftViewAndSearch_ShouldSearchOnlyDraftSubset()
    {
        var service = CreateService(
            [Draft("wf-alpha", "Draft Alpha", "", DateTimeOffset.Parse("2026-08-01T00:00:00Z"))],
            [Committed("wf-beta", "Draft Alpha", "m-beta", "svc-beta", DateTimeOffset.Parse("2026-08-02T00:00:00Z"))]);

        var response = await service.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            ScopeWorkflowCatalogueView.Drafts,
            Query: "Draft Alpha"));

        response.Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-alpha");
    }

    private static AppScopedWorkflowCatalogueService CreateService(
        IReadOnlyList<StudioWorkflowDraftRecord> drafts,
        IReadOnlyList<ScopeWorkflowSummary> committedWorkflows)
    {
        var workspacePort = new RecordingStudioWorkspacePorts(drafts.Select(static draft => new ScopedDraft(ScopeId, draft)));
        var draftService = new AppScopedWorkflowService(
            new StubWorkflowYamlDocumentService(),
            new NoopCapabilityAdmissionService(),
            workspacePort,
            workspaceCommandPort: null);
        return new AppScopedWorkflowCatalogueService(
            draftService,
            new StubScopeWorkflowQueryPort(committedWorkflows));
    }

    private static StudioWorkflowDraftRecord Draft(
        string workflowId,
        string name,
        string description,
        DateTimeOffset updatedAtUtc) =>
        new(
            workflowId,
            name,
            $"{workflowId}.yaml",
            $"scope://{ScopeId}/{workflowId}.yaml",
            $"scope:{ScopeId}",
            ScopeId,
            $"name: {name}\ndescription: {description}\nsteps: []\n",
            Layout: null,
            updatedAtUtc,
            updatedAtUtc,
            1);

    private static ScopeWorkflowSummary Committed(
        string workflowId,
        string displayName,
        string memberId,
        string publishedServiceId,
        DateTimeOffset updatedAtUtc) =>
        new(
            ScopeId,
            workflowId,
            displayName,
            ServiceKeys.Build(ScopeId, "workflow-app", "user:scope-alpha-token", publishedServiceId),
            displayName,
            memberId,
            "rev-alpha",
            publishedServiceId,
            ServiceDeploymentStatus.Active.ToString(),
            updatedAtUtc);

    private sealed class StubWorkflowYamlDocumentService : IWorkflowYamlDocumentService
    {
        public WorkflowParseResult Parse(string yaml)
        {
            var document = new WorkflowDocument
            {
                Name = ReadScalar(yaml, "name") ?? "workflow",
                Description = ReadScalar(yaml, "description") ?? string.Empty,
            };
            return new WorkflowParseResult(document, []);
        }

        public string Serialize(WorkflowDocument document) =>
            $"name: {document.Name}\ndescription: {document.Description}\nsteps: []\n";

        private static string? ReadScalar(string yaml, string key)
        {
            foreach (var line in yaml.Split('\n'))
            {
                var trimmed = line.Trim();
                var prefix = $"{key}:";
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    return trimmed[prefix.Length..].Trim();
            }

            return null;
        }
    }

    private sealed class NoopCapabilityAdmissionService : IWorkflowExternalCapabilityAdmissionService
    {
        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowCapabilityAdmissionPlan());

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowCapabilityAdmissionPlan());
    }

    private sealed class StubScopeWorkflowQueryPort(IReadOnlyList<ScopeWorkflowSummary> workflows) : IScopeWorkflowQueryPort
    {
        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default)
        {
            scopeId.Should().Be(ScopeId);
            return Task.FromResult(workflows);
        }

        public Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(string scopeId, string workflowId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(string scopeId, string workflowId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(string scopeId, string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
