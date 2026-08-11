using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class AppScopedWorkflowCatalogueServiceTests
{
    private const string ScopeId = "scope-alpha";

    [Fact]
    public async Task QueryAsync_ShouldDelegateToWorkflowCatalogueQueryPort()
    {
        var expected = new ScopeWorkflowCatalogueResponse(
            [],
            null,
            new ScopeWorkflowCatalogueFreshness(null, "test freshness"),
            new ScopeWorkflowCatalogueSearchContract([], "test", "FormKC", 128, "empty", "workflowId"));
        var queryPort = new RecordingWorkflowCatalogueQueryPort(expected);
        var service = new AppScopedWorkflowCatalogueService(queryPort);
        var query = new ScopeWorkflowCatalogueQuery(ScopeId, ScopeWorkflowCatalogueView.Drafts, "审批", "1", 10);

        var response = await service.QueryAsync(query);

        response.Should().BeSameAs(expected);
        queryPort.Calls.Should().ContainSingle().Which.Should().Be(query);
    }

    [Fact]
    public async Task QueryAsync_ShouldMergeDraftOnlyCommittedOnlyAndOverlappingRowsByWorkflowId()
    {
        var port = CreatePort(
            Draft("wf-alpha", "Draft Alpha", "draft alpha description", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Draft("wf-overlap", "Draft Overlap", "draft overlap description", DateTimeOffset.Parse("2026-08-03T00:00:00Z")),
            Committed("wf-beta", "Committed Beta", "m-beta", "svc-beta", DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
            Committed("wf-overlap", "Committed Overlap", "m-overlap", "svc-overlap", DateTimeOffset.Parse("2026-08-04T00:00:00Z")));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId));

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
        overlap.Committed!.ActorId.Should().Be("actor-svc-overlap");
        overlap.Committed.DeploymentId.Should().Be("dep-svc-overlap");
        overlap.MemberId.Should().Be("m-overlap");
        overlap.PublishedServiceId.Should().Be("svc-overlap");
        response.Freshness.RefreshWatermarkUtc.Should().Be(DateTimeOffset.Parse("2026-08-04T00:00:00Z"));
    }

    [Fact]
    public async Task QueryAsync_WithDraftView_ShouldReturnDraftSubsetWithCommittedFacts()
    {
        var port = CreatePort(
            Draft("wf-alpha", "Draft Alpha", "draft alpha description", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Draft("wf-overlap", "Draft Overlap", "draft overlap description", DateTimeOffset.Parse("2026-08-03T00:00:00Z")),
            Committed("wf-beta", "Committed Beta", "m-beta", "svc-beta", DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
            Committed("wf-overlap", "Committed Overlap", "m-overlap", "svc-overlap", DateTimeOffset.Parse("2026-08-04T00:00:00Z")));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            ScopeWorkflowCatalogueView.Drafts));

        response.Items.Select(static item => item.WorkflowId).Should().Equal("wf-overlap", "wf-alpha");
        response.Items[0].HasCommittedSource.Should().BeTrue();
        response.Items[0].Capabilities.Activity.Available.Should().BeTrue();
    }

    [Fact]
    public async Task QueryAsync_ShouldSearchNameDescriptionChineseTextAndWorkflowIdPrefix()
    {
        var port = CreatePort(
            Draft("wf-alpha", "Alpha Draft", "Handles 审批 flow", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Draft("wf-gamma", "Gamma Draft", "Other", DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
            Committed("wf-beta", "Billing Review", "m-beta", "svc-beta", DateTimeOffset.Parse("2026-08-03T00:00:00Z")));

        (await port.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId, Query: "alpha")))
            .Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-alpha");
        (await port.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId, Query: "审批")))
            .Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-alpha");
        (await port.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId, Query: "WF-B")))
            .Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-beta");
        (await port.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId, Query: "missing")))
            .Items.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAsync_ShouldSearchBeforeCursorPagination()
    {
        var port = CreatePort(
            Draft("wf-alpha", "Searchable Alpha", "same", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Draft("wf-beta", "Searchable Beta", "same", DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
            Draft("wf-gamma", "Other", "same", DateTimeOffset.Parse("2026-08-03T00:00:00Z")));

        var firstPage = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            Query: "searchable",
            Take: 1));
        var secondPage = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(
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
        var port = CreatePort(
            Draft("wf-alpha", "Draft Alpha", "", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Committed("wf-beta", "Draft Alpha", "m-beta", "svc-beta", DateTimeOffset.Parse("2026-08-02T00:00:00Z")));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            ScopeWorkflowCatalogueView.Drafts,
            Query: "Draft Alpha"));

        response.Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-alpha");
    }

    [Fact]
    public async Task QueryAsync_ShouldReportSourceWatermarkBeforeViewAndSearchFiltering()
    {
        var port = CreatePort(
            Draft("wf-alpha", "Draft Alpha", "", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Committed("wf-beta", "Committed Beta", "m-beta", "svc-beta", DateTimeOffset.Parse("2026-08-05T00:00:00Z")));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            ScopeWorkflowCatalogueView.Drafts,
            Query: "Draft Alpha"));

        response.Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-alpha");
        response.Freshness.RefreshWatermarkUtc.Should().Be(DateTimeOffset.Parse("2026-08-05T00:00:00Z"));
    }

    [Fact]
    public async Task QueryAsync_ShouldKeepSameNameWorkflowsAndTypedIdentitiesSeparate()
    {
        var port = CreatePort(
            Draft("wf-alpha", "Review", "draft", DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Committed("wf-beta", "Review", "m-alpha", "svc-alpha", DateTimeOffset.Parse("2026-08-02T00:00:00Z"), teamId: "team-a", lastBoundRevisionId: "rev-alpha"));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId, Query: "Review"));

        response.Items.Select(static item => item.WorkflowId).Should().Equal("wf-beta", "wf-alpha");
        var committed = response.Items[0];
        committed.WorkflowId.Should().Be("wf-beta");
        committed.MemberId.Should().Be("m-alpha");
        committed.PublishedServiceId.Should().Be("svc-alpha");
        committed.TeamId.Should().Be("team-a");
        committed.LastBoundRevisionId.Should().Be("rev-alpha");
        committed.Committed!.DeploymentId.Should().Be("dep-svc-alpha");
    }

    private static ProjectionWorkflowCatalogueQueryPort CreatePort(
        params ScopeWorkflowCatalogueSourceDocument[] documents) =>
        new(new StubScopeWorkflowCatalogueSourceReader(documents));

    private static ScopeWorkflowCatalogueSourceDocument Draft(
        string workflowId,
        string name,
        string description,
        DateTimeOffset updatedAtUtc) =>
        Source(
            workflowId,
            ScopeWorkflowCatalogueSourceDocument.DraftSourceKind,
            name,
            description,
            updatedAtUtc);

    private static ScopeWorkflowCatalogueSourceDocument Committed(
        string workflowId,
        string displayName,
        string memberId,
        string publishedServiceId,
        DateTimeOffset updatedAtUtc,
        string teamId = "team-alpha",
        string lastBoundRevisionId = "rev-alpha") =>
        Source(
            workflowId,
            ScopeWorkflowCatalogueSourceDocument.CommittedSourceKind,
            displayName,
            string.Empty,
            updatedAtUtc,
            serviceKey: $"scope-alpha:workflow-app:user:scope-alpha-token:{publishedServiceId}",
            workflowName: displayName,
            committedActorId: $"actor-{publishedServiceId}",
            activeRevisionId: $"active-{publishedServiceId}",
            deploymentId: $"dep-{publishedServiceId}",
            deploymentStatus: "Active",
            publishedServiceId: publishedServiceId,
            teamId: teamId,
            memberId: memberId,
            lastBoundRevisionId: lastBoundRevisionId);

    private static ScopeWorkflowCatalogueSourceDocument Source(
        string workflowId,
        string sourceKind,
        string name,
        string description,
        DateTimeOffset updatedAtUtc,
        string serviceKey = "",
        string workflowName = "",
        string committedActorId = "",
        string activeRevisionId = "",
        string deploymentId = "",
        string deploymentStatus = "",
        string publishedServiceId = "",
        string teamId = "",
        string memberId = "",
        string lastBoundRevisionId = "") =>
        new()
        {
            Id = $"{ScopeId}:{workflowId}:{sourceKind}:{publishedServiceId}",
            ActorId = $"catalogue-source:{ScopeId}:{workflowId}:{sourceKind}:{publishedServiceId}",
            StateVersion = updatedAtUtc.ToUnixTimeMilliseconds(),
            LastEventId = $"event-{workflowId}-{sourceKind}-{publishedServiceId}",
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAtUtc),
            ScopeId = ScopeId,
            WorkflowId = workflowId,
            SourceKind = sourceKind,
            Name = name,
            Description = description,
            SourceUpdatedAtUtc = updatedAtUtc,
            ServiceKey = serviceKey,
            WorkflowName = workflowName,
            CommittedActorId = committedActorId,
            ActiveRevisionId = activeRevisionId,
            DeploymentId = deploymentId,
            DeploymentStatus = deploymentStatus,
            PublishedServiceId = publishedServiceId,
            TeamId = teamId,
            MemberId = memberId,
            LastBoundRevisionId = lastBoundRevisionId,
        };

    private sealed class StubScopeWorkflowCatalogueSourceReader(IReadOnlyList<ScopeWorkflowCatalogueSourceDocument> documents)
        : IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string>
    {
        public Task<ScopeWorkflowCatalogueSourceDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(documents.FirstOrDefault(document => string.Equals(document.Id, key, StringComparison.Ordinal)));

        public Task<ProjectionDocumentQueryResult<ScopeWorkflowCatalogueSourceDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            var scopeFilter = query.Filters.FirstOrDefault(filter =>
                string.Equals(filter.FieldPath, nameof(ScopeWorkflowCatalogueSourceDocument.ScopeId), StringComparison.Ordinal));
            var scopeId = scopeFilter?.Value.RawValue as string;
            var items = string.IsNullOrWhiteSpace(scopeId)
                ? documents
                : documents.Where(document => string.Equals(document.ScopeId, scopeId, StringComparison.Ordinal)).ToList();

            return Task.FromResult(new ProjectionDocumentQueryResult<ScopeWorkflowCatalogueSourceDocument>
            {
                Items = items.Take(query.Take).ToList(),
                NextCursor = null,
                TotalCount = items.Count,
            });
        }
    }

    private sealed class RecordingWorkflowCatalogueQueryPort(ScopeWorkflowCatalogueResponse response) : IWorkflowCatalogueQueryPort
    {
        public List<ScopeWorkflowCatalogueQuery> Calls { get; } = [];

        public Task<ScopeWorkflowCatalogueResponse> QueryAsync(
            ScopeWorkflowCatalogueQuery query,
            CancellationToken ct = default)
        {
            Calls.Add(query);
            return Task.FromResult(response);
        }
    }
}
