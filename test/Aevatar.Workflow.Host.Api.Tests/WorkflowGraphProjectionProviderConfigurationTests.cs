using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Extensions.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowGraphProjectionProviderConfigurationTests
{
    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldAllowDisabledGraphInProduction()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "false",
                ["Projection:Policies:Environment"] = "Production",
                ["Projection:Policies:DenyInMemoryDocumentReadStore"] = "true",
                ["Projection:Policies:DenyInMemoryGraphFactStore"] = "true",
            })
            .Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionGraphStore>()
            .Should().BeOfType<DisabledProjectionGraphStore>();
        provider.GetRequiredService<IVersionedProjectionGraphStore>()
            .Should().BeSameAs(provider.GetRequiredService<IProjectionGraphStore>());
        provider.GetRequiredService<ProjectionGraphProviderStatus>()
            .Should().Be(new ProjectionGraphProviderStatus("Disabled", Enabled: false));
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRegisterDisabledGraph_WhenDocumentsAlreadyRegistered()
    {
        var services = new ServiceCollection();
        services.AddWorkflowProjectionReadModelProviders(new ConfigurationBuilder().Build());
        services.RemoveAll<IProjectionGraphStore>();
        services.RemoveAll<IVersionedProjectionGraphStore>();
        services.RemoveAll<ProjectionGraphProviderStatus>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "false",
            })
            .Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionGraphStore>()
            .Should().BeOfType<DisabledProjectionGraphStore>();
        provider.GetRequiredService<IVersionedProjectionGraphStore>()
            .Should().BeSameAs(provider.GetRequiredService<IProjectionGraphStore>());
        provider.GetRequiredService<ProjectionGraphProviderStatus>().Enabled.Should().BeFalse();
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldNotInferNeo4jEnabledFromUri()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Graph:Providers:Neo4j:Uri"] = "bolt://stale-neo4j:7687",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "false",
            })
            .Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ProjectionGraphProviderStatus>()
            .Should().Be(new ProjectionGraphProviderStatus("Disabled", Enabled: false));
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRejectExistingProviderThatConflictsWithConfiguration()
    {
        var services = new ServiceCollection();
        services.AddInMemoryGraphProjectionStore();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "false",
            })
            .Build();

        var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*existing graph projection provider registration*Disabled*");
    }
}
