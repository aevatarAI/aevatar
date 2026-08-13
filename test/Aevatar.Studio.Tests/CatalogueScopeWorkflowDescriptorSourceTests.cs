using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class CatalogueScopeWorkflowDescriptorSourceTests
{
    [Fact]
    public async Task FindByWorkflowIdAsync_ShouldMapCommittedCatalogueRowToPublishedServiceDescriptor()
    {
        var updatedAt = DateTimeOffset.Parse("2026-08-13T09:20:12Z");
        var row = CommittedRow("wf-alpha", "svc-alpha", "Alpha Workflow", updatedAt);
        var source = new CatalogueScopeWorkflowDescriptorSource(new PagedWorkflowCatalogueQueryPort([row]));

        var result = await source.FindByWorkflowIdAsync("scope-1", "wf-alpha");

        result.Should().ContainSingle();
        result[0].WorkflowId.Should().Be("wf-alpha");
        result[0].ServiceAppId.Should().Be("studio");
        result[0].ServiceNamespace.Should().Be("default");
        result[0].PublishedServiceId.Should().Be("svc-alpha");
        result[0].DisplayName.Should().Be("Alpha Workflow");
        result[0].UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public async Task ListAsync_ShouldPageUntilEnoughCommittedDescriptorsAreFound()
    {
        var updatedAt = DateTimeOffset.Parse("2026-08-13T09:20:12Z");
        var source = new CatalogueScopeWorkflowDescriptorSource(new PagedWorkflowCatalogueQueryPort(
            [DraftRow("wf-draft-1"), DraftRow("wf-draft-2")],
            [CommittedRow("wf-alpha", "svc-alpha", "Alpha Workflow", updatedAt),
                CommittedRow("wf-beta", "svc-beta", "Beta Workflow", updatedAt)]));

        var result = await source.ListAsync("scope-1", 2);

        result.Should().HaveCount(2);
        result.Select(descriptor => descriptor.WorkflowId).Should().Equal("wf-alpha", "wf-beta");
    }

    [Fact]
    public async Task FindByWorkflowIdAsync_ShouldPageUntilExactWorkflowIdIsFound()
    {
        var updatedAt = DateTimeOffset.Parse("2026-08-13T09:20:12Z");
        var source = new CatalogueScopeWorkflowDescriptorSource(new PagedWorkflowCatalogueQueryPort(
            [CommittedRow("wf-alpha-copy-1", "svc-copy-1", "Copy 1", updatedAt)],
            [CommittedRow("wf-alpha", "svc-alpha", "Alpha Workflow", updatedAt)]));

        var result = await source.FindByWorkflowIdAsync("scope-1", "wf-alpha");

        result.Should().ContainSingle();
        result[0].WorkflowId.Should().Be("wf-alpha");
        result[0].PublishedServiceId.Should().Be("svc-alpha");
    }

    private static ScopeWorkflowCatalogueRow CommittedRow(
        string workflowId,
        string serviceId,
        string name,
        DateTimeOffset updatedAt) =>
        new(
            "scope-1",
            workflowId,
            name,
            "",
            HasDraftSource: false,
            HasCommittedSource: true,
            updatedAt,
            "service",
            Capabilities(),
            updatedAt,
            new ScopeWorkflowCatalogueCommittedFacts(
                $"scope-1:studio:default:{serviceId}",
                name,
                $"actor-{workflowId}",
                $"rev-{workflowId}",
                $"dep-{workflowId}",
                "Active",
                "studio",
                "default"),
            serviceId);

    private static ScopeWorkflowCatalogueRow DraftRow(string workflowId) =>
        new(
            "scope-1",
            workflowId,
            workflowId,
            "",
            HasDraftSource: true,
            HasCommittedSource: false,
            DateTimeOffset.Parse("2026-08-13T09:20:12Z"),
            "draft",
            Capabilities(),
            DateTimeOffset.Parse("2026-08-13T09:20:12Z"));

    private static ScopeWorkflowCatalogueRowCapabilities Capabilities() =>
        new(
            new ScopeWorkflowCatalogueActionCapability(true),
            new ScopeWorkflowCatalogueActionCapability(true),
            new ScopeWorkflowCatalogueActionCapability(false),
            new ScopeWorkflowCatalogueActionCapability(false));

    private sealed class PagedWorkflowCatalogueQueryPort(params ScopeWorkflowCatalogueRow[][] pages) : IWorkflowCatalogueQueryPort
    {
        public Task<ScopeWorkflowCatalogueResponse> QueryAsync(
            ScopeWorkflowCatalogueQuery query,
            CancellationToken ct = default)
        {
            var pageIndex = string.IsNullOrWhiteSpace(query.Cursor) ? 0 : int.Parse(query.Cursor);
            var items = pageIndex < pages.Length
                ? pages[pageIndex]
                    .Where(row => string.Equals(row.ScopeId, query.ScopeId, StringComparison.Ordinal))
                    .Where(row => MatchesQuery(row, query.Query))
                    .ToArray()
                : [];
            var nextPageToken = pageIndex + 1 < pages.Length ? (pageIndex + 1).ToString() : null;
            return Task.FromResult(new ScopeWorkflowCatalogueResponse(
                items,
                nextPageToken,
                new ScopeWorkflowCatalogueFreshness(null, "test"),
                new ScopeWorkflowCatalogueSearchContract([], "test", "FormKC", 128, "empty", "workflowId")));
        }

        private static bool MatchesQuery(ScopeWorkflowCatalogueRow row, string? query) =>
            string.IsNullOrWhiteSpace(query) ||
            row.WorkflowId.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.WorkflowId, query, StringComparison.Ordinal);
    }
}
