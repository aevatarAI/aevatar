using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StatusDashboard.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class StatusDashboardServiceCollectionExtensionsTests
{
    [Fact]
    public void AddStatusDashboard_RegistersCommittedStateProjectionActivationHook()
    {
        using var provider = new ServiceCollection()
            .AddStatusDashboard(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        provider.GetService<ProjectionActivationPlanDispatcher>()
            .Should().NotBeNull("the committed-state hook dispatches activation plans through the shared dispatcher");
        provider.GetServices<ICommittedStatePublicationHook>()
            .Should().ContainSingle(hook => hook is CommittedStateProjectionActivationHook);
        provider.GetServices<IProjectionActivationPlanProvider>()
            .Should().ContainSingle(planProvider =>
                planProvider is HealthProbeCommittedStateProjectionActivationPlanProvider);
    }

    [Fact]
    public void AddStatusDashboard_RegistersHealthProbeDocumentStore()
    {
        using var provider = new ServiceCollection()
            .AddStatusDashboard(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        provider.GetService<IProjectionDocumentReader<HealthProbeTargetDocument, string>>()
            .Should().NotBeNull("the status dashboard query port reads the materialized current-state document");
        provider.GetService<IProjectionDocumentWriter<HealthProbeTargetDocument>>()
            .Should().NotBeNull("the health probe projector must be able to upsert current-state documents");
    }
}
