using Aevatar.GAgents.StatusDashboard.Configuration;
using Aevatar.GAgents.StatusDashboard.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class StatusDashboardServiceCollectionExtensionsTests
{
    [Fact]
    public void AddStatusDashboard_DoesNotRegisterProjectionServices()
    {
        var services = new ServiceCollection()
            .AddStatusDashboard(new ConfigurationBuilder().Build());

        services.Should().NotContain(descriptor =>
            IsProjectionType(descriptor.ServiceType) ||
            IsProjectionType(descriptor.ImplementationType));
    }

    [Fact]
    public void AddStatusDashboard_RegistersProviderNeutralHealthProbeServices()
    {
        var services = new ServiceCollection()
            .AddStatusDashboard(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHealthStatusQueryPort) &&
            descriptor.ImplementationType == typeof(HealthStatusQueryPort));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHealthProbeOperationalSnapshotStore) &&
            descriptor.ImplementationType == typeof(InMemoryHealthProbeOperationalSnapshotStore));
    }

    private static bool IsProjectionType(Type? type) =>
        type?.Namespace?.StartsWith("Aevatar.CQRS.Projection", StringComparison.Ordinal) == true;
}
