using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
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
    public void AddStatusDashboard_RegistersProviderNeutralHealthProbeServices()
    {
        var services = new ServiceCollection()
            .AddStatusDashboard(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<HealthProbeTargetDocument>));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHealthStatusQueryPort) &&
            descriptor.ImplementationType == typeof(HealthStatusQueryPort));
    }
}
