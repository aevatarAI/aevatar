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
        var row = new ScopeWorkflowCatalogueRow(
            "scope-1",
            "wf-alpha",
            "Alpha Workflow",
            "",
            HasDraftSource: false,
            HasCommittedSource: true,
            updatedAt,
            "service",
            new ScopeWorkflowCatalogueRowCapabilities(
                new ScopeWorkflowCatalogueActionCapability(true),
                new ScopeWorkflowCatalogueActionCapability(true),
                new ScopeWorkflowCatalogueActionCapability(false),
                new ScopeWorkflowCatalogueActionCapability(false)),
            updatedAt,
            new ScopeWorkflowCatalogueCommittedFacts(
                "scope-1:studio:default:svc-alpha",
                "Alpha Workflow",
                "actor-alpha",
                "rev-alpha",
                "dep-alpha",
                "Active",
                "studio",
                "default"),
            "svc-alpha");
        var source = new CatalogueScopeWorkflowDescriptorSource(new FixedWorkflowCatalogueQueryPort(row));

        var result = await source.FindByWorkflowIdAsync("scope-1", "wf-alpha");

        result.Should().ContainSingle();
        result[0].WorkflowId.Should().Be("wf-alpha");
        result[0].ServiceAppId.Should().Be("studio");
        result[0].ServiceNamespace.Should().Be("default");
        result[0].PublishedServiceId.Should().Be("svc-alpha");
        result[0].DisplayName.Should().Be("Alpha Workflow");
        result[0].UpdatedAt.Should().Be(updatedAt);
    }

    private sealed class FixedWorkflowCatalogueQueryPort(params ScopeWorkflowCatalogueRow[] rows) : IWorkflowCatalogueQueryPort
    {
        public Task<ScopeWorkflowCatalogueResponse> QueryAsync(
            ScopeWorkflowCatalogueQuery query,
            CancellationToken ct = default)
        {
            var items = rows
                .Where(row => string.Equals(row.ScopeId, query.ScopeId, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult(new ScopeWorkflowCatalogueResponse(
                items,
                null,
                new ScopeWorkflowCatalogueFreshness(null, "test"),
                new ScopeWorkflowCatalogueSearchContract([], "test", "FormKC", 128, "empty", "workflowId")));
        }
    }
}
