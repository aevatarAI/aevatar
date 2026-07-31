using Aevatar.GAgents.StatusDashboard.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class HealthStatusQueryPortTests
{
    [Fact]
    public async Task ListAllAsync_ReturnsOnlyCurrentManifestTargets()
    {
        var current = new HealthProbeOperationalSnapshot
        {
            Target = new HealthProbeTargetDescriptor
            {
                Slug = "self-liveness",
                DisplayName = "HTTP API (liveness)",
                Category = "feature",
            },
        };
        var retiredAuthGate = new HealthProbeOperationalSnapshot
        {
            Target = new HealthProbeTargetDescriptor
            {
                Slug = "responses-api-auth-gate",
                DisplayName = "Responses API auth gate",
                Category = "feature",
            },
        };
        var retired = new HealthProbeOperationalSnapshot
        {
            Target = new HealthProbeTargetDescriptor
            {
                Slug = "responses-forward-team-08-nyxid-proxy-e2e",
                DisplayName = "Responses -> Studio Team 08 NyxID proxy e2e",
                Category = "feature",
            },
        };
        var retiredSingularRouteProbe = new HealthProbeOperationalSnapshot
        {
            Target = new HealthProbeTargetDescriptor
            {
                Slug = "chat-completion-api-singular-route",
                DisplayName = "OpenAI Chat Completion singular route",
                Category = "feature",
            },
        };
        var store = new InMemoryHealthProbeOperationalSnapshotStore();
        foreach (var snapshot in new[] { current, retiredAuthGate, retired, retiredSingularRouteProbe })
            await store.UpsertAsync(snapshot);
        var port = new HealthStatusQueryPort(
            store,
            Options.Create(new StatusDashboardOptions()));

        var results = await port.ListAllAsync();

        results.Select(static x => x.Target.Slug).Should().Equal("self-liveness");
        (await port.GetBySlugAsync(retiredAuthGate.Target.Slug)).Should().BeNull();
        (await port.GetBySlugAsync(retired.Target.Slug)).Should().BeNull();
        (await port.GetBySlugAsync(retiredSingularRouteProbe.Target.Slug)).Should().BeNull();
        (await port.GetBySlugAsync(current.Target.Slug)).Should().NotBeNull();
    }
}
