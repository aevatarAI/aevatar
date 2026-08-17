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
    public async Task QueryAsync_ShouldReadAggregateRowsWithoutJoiningSources()
    {
        var port = CreatePort(
            Row("wf-alpha", "Draft Alpha", "draft alpha description", true, false, DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Row("wf-beta", "Published Beta", "", false, true, DateTimeOffset.Parse("2026-08-02T00:00:00Z"), "published-service-beta"),
            Row("wf-overlap", "Draft Overlap", "draft overlap description", true, true, DateTimeOffset.Parse("2026-08-03T00:00:00Z"), "published-service-overlap"));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId));

        response.Items.Select(static item => item.WorkflowId).Should().Equal("wf-overlap", "wf-beta", "wf-alpha");
        var overlap = response.Items[0];
        overlap.HasDraftSource.Should().BeTrue();
        overlap.HasCommittedSource.Should().BeTrue();
        overlap.Name.Should().Be("Draft Overlap");
        overlap.Description.Should().Be("draft overlap description");
        overlap.UpdatedAtSource.Should().Be(ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind);
        overlap.Capabilities.Open.Available.Should().BeTrue();
        overlap.Capabilities.Activity.Available.Should().BeTrue();
        overlap.Capabilities.Rename.Available.Should().BeTrue();
        overlap.Capabilities.Delete.Available.Should().BeTrue();
        overlap.Committed.Should().NotBeNull();
        overlap.Committed!.ActorId.Should().Be("actor-wf-overlap");
        overlap.Committed.DeploymentId.Should().Be("dep-wf-overlap");
        overlap.Committed.ServiceAppId.Should().Be("studio");
        overlap.Committed.ServiceNamespace.Should().Be("default");
        overlap.PublishedServiceId.Should().Be("published-service-overlap");
        response.Freshness.RefreshWatermarkUtc.Should().Be(DateTimeOffset.Parse("2026-08-03T00:00:00Z"));
    }

    [Fact]
    public async Task QueryAsync_ShouldHideArchivedRowsFromDefaultCatalogueView()
    {
        var port = CreatePort(
            Row("wf-archived", "Archived Workflow", "archived workflow", true, true, DateTimeOffset.Parse("2026-08-05T00:00:00Z"), "published-service-archived", "Deactivated"),
            Row("wf-active", "Active Workflow", "active workflow", false, true, DateTimeOffset.Parse("2026-08-04T00:00:00Z"), "published-service-active"),
            Row("wf-draft", "Draft Workflow", "draft workflow", true, false, DateTimeOffset.Parse("2026-08-03T00:00:00Z")));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            Take: 1));

        response.Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-active");
        response.Items.Select(static item => item.WorkflowId).Should().NotContain("wf-archived");
        response.NextPageToken.Should().Be("1");
    }

    [Fact]
    public async Task QueryAsync_WithDraftView_ShouldExcludeArchivedRowsEvenIfTheyHaveDraftSource()
    {
        var port = CreatePort(
            Row("wf-archived", "Archived Draft", "archived draft description", true, true, DateTimeOffset.Parse("2026-08-03T00:00:00Z"), "published-service-archived", "Deactivated"),
            Row("wf-draft", "Draft Alpha", "draft alpha description", true, false, DateTimeOffset.Parse("2026-08-02T00:00:00Z")));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            ScopeWorkflowCatalogueView.Drafts));

        response.Items.Select(static item => item.WorkflowId).Should().Equal("wf-draft");
        response.Items[0].HasCommittedSource.Should().BeFalse();
    }

    [Fact]
    public async Task QueryAsync_WithArchivedView_ShouldReturnOnlyArchivedRowsWithCommittedFacts()
    {
        var port = CreatePort(
            Row("wf-archived", "Archived Draft", "archived draft description", true, true, DateTimeOffset.Parse("2026-08-03T00:00:00Z"), "published-service-archived", "Deactivated"),
            Row("wf-active", "Active Beta", "", false, true, DateTimeOffset.Parse("2026-08-02T00:00:00Z"), "published-service-active"),
            Row("wf-draft", "Draft Alpha", "draft alpha description", true, false, DateTimeOffset.Parse("2026-08-01T00:00:00Z")));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            ScopeWorkflowCatalogueView.Archived));

        var row = response.Items.Should().ContainSingle().Subject;
        row.WorkflowId.Should().Be("wf-archived");
        row.HasDraftSource.Should().BeTrue();
        row.HasCommittedSource.Should().BeTrue();
        row.Committed.Should().NotBeNull();
        row.Committed!.ServiceKey.Should().Be("service-key:wf-archived");
        row.Committed.WorkflowName.Should().Be("wf-archived");
        row.Committed.ActorId.Should().Be("actor-wf-archived");
        row.Committed.ActiveRevisionId.Should().Be("active-wf-archived");
        row.Committed.DeploymentId.Should().Be("dep-wf-archived");
        row.Committed.ServiceAppId.Should().Be("studio");
        row.Committed.ServiceNamespace.Should().Be("default");
        row.Committed!.DeploymentStatus.Should().Be("Deactivated");
        row.PublishedServiceId.Should().Be("published-service-archived");
        row.Capabilities.Delete.Available.Should().BeFalse();
        row.Capabilities.Delete.UnavailableReason.Should().Be("workflow_archived");
    }

    [Fact]
    public async Task QueryAsync_ShouldSearchNameDescriptionChineseTextAndWorkflowIdPrefix()
    {
        var port = CreatePort(
            Row("wf-alpha", "Alpha Draft", "Handles 审批 flow", true, false, DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Row("wf-gamma", "Gamma Draft", "Other", true, false, DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
            Row("wf-beta", "Billing Service", "", false, true, DateTimeOffset.Parse("2026-08-03T00:00:00Z")));

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
            Row("wf-alpha", "Searchable Alpha", "same", true, false, DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Row("wf-beta", "Searchable Beta", "same", true, false, DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
            Row("wf-gamma", "Other", "same", true, false, DateTimeOffset.Parse("2026-08-03T00:00:00Z")));

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
            Row("wf-alpha", "Draft Alpha", "", true, false, DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Row("wf-beta", "Draft Alpha", "", false, true, DateTimeOffset.Parse("2026-08-02T00:00:00Z")));

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
            Row("wf-alpha", "Draft Alpha", "", true, false, DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Row("wf-beta", "Published Beta", "", false, true, DateTimeOffset.Parse("2026-08-05T00:00:00Z")));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(
            ScopeId,
            ScopeWorkflowCatalogueView.Drafts,
            Query: "Draft Alpha"));

        response.Items.Should().ContainSingle().Which.WorkflowId.Should().Be("wf-alpha");
        response.Freshness.RefreshWatermarkUtc.Should().Be(DateTimeOffset.Parse("2026-08-05T00:00:00Z"));
    }

    [Fact]
    public async Task QueryAsync_ShouldDisablePublishedOnlyDraftActions()
    {
        var port = CreatePort(
            Row("wf-beta", "Published Beta", "", false, true, DateTimeOffset.Parse("2026-08-02T00:00:00Z")));

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId));

        var row = response.Items.Should().ContainSingle().Subject;
        row.HasCommittedSource.Should().BeTrue();
        row.Capabilities.Activity.Available.Should().BeTrue();
        row.Capabilities.Rename.Available.Should().BeFalse();
        row.Capabilities.Rename.UnavailableReason.Should().Be("draft_source_missing");
        row.Capabilities.Delete.Available.Should().BeFalse();
        row.Capabilities.Delete.UnavailableReason.Should().Be("draft_source_missing");
    }

    [Fact]
    public async Task QueryAsync_ShouldDrainRowReaderPagesBeforeFiltering()
    {
        var reader = new StubScopeWorkflowCatalogueRowReader([
            Row("wf-alpha", "Draft Alpha", "", true, false, DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Row("wf-beta", "Draft Beta", "", true, false, DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
        ], pageSize: 1);
        var port = new ProjectionWorkflowCatalogueQueryPort(reader);

        var response = await port.QueryAsync(new ScopeWorkflowCatalogueQuery(ScopeId));

        response.Items.Select(static item => item.WorkflowId).Should().Equal("wf-beta", "wf-alpha");
    }

    private static ProjectionWorkflowCatalogueQueryPort CreatePort(
        params ScopeWorkflowCatalogueRowDocument[] documents) =>
        new(new StubScopeWorkflowCatalogueRowReader(documents));

    private static ScopeWorkflowCatalogueRowDocument Row(
        string workflowId,
        string name,
        string description,
        bool hasDraftSource,
        bool hasPublishedSource,
        DateTimeOffset updatedAtUtc,
        string? publishedServiceId = null,
        string? deploymentStatus = null) =>
        new()
        {
            Id = $"{ScopeId}:workflow:{workflowId}",
            ActorId = $"catalogue-row:{workflowId}",
            StateVersion = updatedAtUtc.ToUnixTimeMilliseconds(),
            LastEventId = $"event-{workflowId}",
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAtUtc),
            ScopeId = ScopeId,
            WorkflowId = workflowId,
            Name = name,
            Description = description,
            HasDraftSource = hasDraftSource,
            HasPublishedSource = hasPublishedSource,
            RowUpdatedAtUtc = updatedAtUtc,
            UpdatedAtSource = hasPublishedSource
                ? ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind
                : ScopeWorkflowCatalogueSourceDocument.DraftSourceKind,
            SourceWatermarkUtc = updatedAtUtc,
            ServiceKey = hasPublishedSource ? $"service-key:{workflowId}" : string.Empty,
            WorkflowName = hasPublishedSource ? workflowId : string.Empty,
            CommittedActorId = hasPublishedSource ? $"actor-{workflowId}" : string.Empty,
            ActiveRevisionId = hasPublishedSource ? $"active-{workflowId}" : string.Empty,
            DeploymentId = hasPublishedSource ? $"dep-{workflowId}" : string.Empty,
            DeploymentStatus = hasPublishedSource ? deploymentStatus ?? "Active" : string.Empty,
            ServiceAppId = hasPublishedSource ? "studio" : string.Empty,
            ServiceNamespace = hasPublishedSource ? "default" : string.Empty,
            PublishedServiceId = hasPublishedSource ? publishedServiceId ?? workflowId : string.Empty,
        };

    private sealed class StubScopeWorkflowCatalogueRowReader(
        IReadOnlyList<ScopeWorkflowCatalogueRowDocument> documents,
        int pageSize = 10_000)
        : IProjectionDocumentReader<ScopeWorkflowCatalogueRowDocument, string>
    {
        public Task<ScopeWorkflowCatalogueRowDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(documents.FirstOrDefault(document => string.Equals(document.Id, key, StringComparison.Ordinal)));

        public Task<ProjectionDocumentQueryResult<ScopeWorkflowCatalogueRowDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            var scopeFilter = query.Filters.FirstOrDefault(filter =>
                string.Equals(filter.FieldPath, nameof(ScopeWorkflowCatalogueRowDocument.ScopeId), StringComparison.Ordinal));
            var scopeId = scopeFilter?.Value.RawValue as string;
            var filteredItems = string.IsNullOrWhiteSpace(scopeId)
                ? documents
                : documents.Where(document => string.Equals(document.ScopeId, scopeId, StringComparison.Ordinal)).ToList();
            var offset = string.IsNullOrWhiteSpace(query.Cursor) ? 0 : int.Parse(query.Cursor);
            var take = Math.Min(query.Take, pageSize);
            var items = filteredItems.Skip(offset).Take(take).ToList();
            var nextOffset = offset + items.Count;

            return Task.FromResult(new ProjectionDocumentQueryResult<ScopeWorkflowCatalogueRowDocument>
            {
                Items = items,
                NextCursor = nextOffset < filteredItems.Count ? nextOffset.ToString() : null,
                TotalCount = filteredItems.Count,
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
