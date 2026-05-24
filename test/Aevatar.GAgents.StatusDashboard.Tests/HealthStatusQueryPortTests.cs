using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StatusDashboard.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class HealthStatusQueryPortTests
{
    [Fact]
    public async Task ListAllAsync_ReturnsOnlyCurrentManifestTargets()
    {
        var current = new HealthProbeTargetDocument
        {
            Id = "responses-api-auth-gate",
            Slug = "responses-api-auth-gate",
            DisplayName = "Responses API auth gate",
            Category = "feature",
        };
        var retired = new HealthProbeTargetDocument
        {
            Id = "responses-forward-team-08-nyxid-proxy-e2e",
            Slug = "responses-forward-team-08-nyxid-proxy-e2e",
            DisplayName = "Responses -> Studio Team 08 NyxID proxy e2e",
            Category = "feature",
        };
        var port = new HealthStatusQueryPort(
            new StaticReader([current, retired]),
            Options.Create(new StatusDashboardOptions()));

        var results = await port.ListAllAsync();

        results.Select(static x => x.Slug).Should().Equal("responses-api-auth-gate");
        (await port.GetBySlugAsync(retired.Slug)).Should().BeNull();
        (await port.GetBySlugAsync(current.Slug)).Should().NotBeNull();
    }

    private sealed class StaticReader(IReadOnlyList<HealthProbeTargetDocument> items)
        : IProjectionDocumentReader<HealthProbeTargetDocument, string>
    {
        public Task<HealthProbeTargetDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(items.FirstOrDefault(x => x.Slug == key));
        }

        public Task<ProjectionDocumentQueryResult<HealthProbeTargetDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<HealthProbeTargetDocument>
            {
                Items = items,
            });
        }
    }
}
