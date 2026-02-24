using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowHostingExtensionsCoverageTests
{
    [Fact]
    public void AddWorkflowCapabilityWithAIDefaults_ShouldValidateBuilder()
    {
        Action act = () => WorkflowCapabilityHostBuilderExtensions.AddWorkflowCapabilityWithAIDefaults(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task AddWorkflowCapabilityWithAIDefaults_ShouldRegisterWorkflowAndAIServices()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.AddWorkflowCapabilityWithAIDefaults(options =>
        {
            options.EnableMCPTools = false;
            options.EnableSkills = false;
            options.ApiKey = "demo-key";
            options.DefaultProvider = "openai";
        });

        builder.Services.Any(x => x.ServiceType == typeof(IWorkflowRunRequestExecutor)).Should().BeTrue();
        builder.Services.Any(x => x.ServiceType == typeof(IWorkflowRunActorPort)).Should().BeTrue();
        builder.Services.Any(x => x.ServiceType == typeof(IDocumentProjectionStore<WorkflowExecutionReport, string>)).Should().BeTrue();

        await using var provider = builder.Services.BuildServiceProvider();
        provider.GetService<ILLMProviderFactory>().Should().NotBeNull();

        var toolSources = provider.GetServices<IAgentToolSource>().ToList();
        toolSources.Should().NotContain(x => x is MCPAgentToolSource);
        toolSources.Should().NotContain(x => x is SkillsAgentToolSource);
    }

    [Fact]
    public void AddWorkflowCapabilityWithAIDefaults_ShouldRegisterReadModelProvidersInHostLayer()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.AddWorkflowCapabilityWithAIDefaults();

        var providerRegistrations = builder.Services
            .Where(x => x.ServiceType == typeof(IProjectionStoreRegistration<IDocumentProjectionStore<WorkflowExecutionReport, string>>))
            .ToList();
        providerRegistrations.Should().HaveCount(1);

        var relationRegistrations = builder.Services
            .Where(x => x.ServiceType == typeof(IProjectionStoreRegistration<IProjectionGraphStore>))
            .ToList();
        relationRegistrations.Should().HaveCount(1);
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_MultipleCalls_ShouldBeIdempotent()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);
        services.AddWorkflowProjectionReadModelProviders(configuration);

        var providerRegistrations = services
            .Where(x => x.ServiceType == typeof(IProjectionStoreRegistration<IDocumentProjectionStore<WorkflowExecutionReport, string>>))
            .ToList();
        providerRegistrations.Should().HaveCount(1);

        var relationRegistrations = services
            .Where(x => x.ServiceType == typeof(IProjectionStoreRegistration<IProjectionGraphStore>))
            .ToList();
        relationRegistrations.Should().HaveCount(1);
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_WhenProvidersAreConfigured_ShouldRegisterConfiguredCombinationOnly()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Provider"] = ProjectionProviderNames.Elasticsearch,
                ["Projection:Graph:Provider"] = ProjectionProviderNames.InMemory,
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
            })
            .Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);

        var providerRegistrations = services
            .Where(x => x.ServiceType == typeof(IProjectionStoreRegistration<IDocumentProjectionStore<WorkflowExecutionReport, string>>))
            .ToList();
        var relationRegistrations = services
            .Where(x => x.ServiceType == typeof(IProjectionStoreRegistration<IProjectionGraphStore>))
            .ToList();
        var selectionOptionsRegistrations = services
            .Where(x => x.ServiceType == typeof(IProjectionStoreSelectionRuntimeOptions))
            .ToList();

        providerRegistrations.Should().HaveCount(1);
        relationRegistrations.Should().HaveCount(1);
        selectionOptionsRegistrations.Should().HaveCount(1);

        using var provider = services.BuildServiceProvider();
        var selectionOptions = provider.GetRequiredService<IProjectionStoreSelectionRuntimeOptions>();
        selectionOptions.DocumentProvider.Should().Be(ProjectionProviderNames.Elasticsearch);
        selectionOptions.GraphProvider.Should().Be(ProjectionProviderNames.InMemory);
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_WhenProviderConfiguredUnknown_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Provider"] = "UnknownProvider",
            })
            .Build();

        Action act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unsupported projection provider*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_WhenPolicyDeniesInMemoryRelationFactStore_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Provider"] = ProjectionProviderNames.Elasticsearch,
                ["Projection:Graph:Provider"] = ProjectionProviderNames.InMemory,
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Policies:DenyInMemoryGraphFactStore"] = "true",
            })
            .Build();

        Action act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*InMemory graph provider is not allowed*");
    }
}
