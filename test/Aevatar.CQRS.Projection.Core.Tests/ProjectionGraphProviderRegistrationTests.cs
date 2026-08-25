using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionGraphProviderRegistrationTests
{
    [Fact]
    public void AddInMemoryGraphProjectionStore_ShouldRegisterEnabledProviderStatus()
    {
        var services = new ServiceCollection();
        services.AddInMemoryGraphProjectionStore();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ProjectionGraphProviderStatus>()
            .Should().Be(new ProjectionGraphProviderStatus("InMemory", Enabled: true));
    }

    [Fact]
    public void AddNeo4jGraphProjectionStore_ShouldRegisterEnabledProviderStatus()
    {
        var services = new ServiceCollection();
        services.AddNeo4jGraphProjectionStore(_ => new Neo4jProjectionGraphStoreOptions());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ProjectionGraphProviderStatus>()
            .Should().Be(new ProjectionGraphProviderStatus("Neo4j", Enabled: true));
    }
}
