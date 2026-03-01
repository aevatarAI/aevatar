using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Hosting;
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
        builder.Services.Any(x => x.ServiceType == typeof(IProjectionDocumentStore<WorkflowExecutionReport, string>)).Should().BeTrue();
        builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .Should()
            .Contain(x => x.Name == "script");

        await using var provider = builder.Services.BuildServiceProvider();
        provider.GetService<ILLMProviderFactory>().Should().NotBeNull();
        provider.GetService<IProjectionDocumentStore<WorkflowExecutionReport, string>>().Should().NotBeNull();

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

        var documentStores = builder.Services
            .Where(x => x.ServiceType == typeof(IProjectionDocumentStore<WorkflowExecutionReport, string>))
            .ToList();
        documentStores.Should().HaveCount(1);

        var graphStores = builder.Services
            .Where(x => x.ServiceType == typeof(IProjectionGraphStore))
            .ToList();
        graphStores.Should().HaveCount(1);
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_MultipleCalls_ShouldBeIdempotent()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);
        services.AddWorkflowProjectionReadModelProviders(configuration);

        var documentStores = services
            .Where(x => x.ServiceType == typeof(IProjectionDocumentStore<WorkflowExecutionReport, string>))
            .ToList();
        documentStores.Should().HaveCount(1);

        var graphStores = services
            .Where(x => x.ServiceType == typeof(IProjectionGraphStore))
            .ToList();
        graphStores.Should().HaveCount(1);
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_WhenDurableProvidersEnabled_ShouldRegisterDurableCombinationOnly()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "false",
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Uri"] = "bolt://localhost:7687",
            })
            .Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);

        var documentStores = services
            .Where(x => x.ServiceType == typeof(IProjectionDocumentStore<WorkflowExecutionReport, string>))
            .ToList();
        var graphStores = services
            .Where(x => x.ServiceType == typeof(IProjectionGraphStore))
            .ToList();

        documentStores.Should().HaveCount(1);
        graphStores.Should().HaveCount(1);
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_WhenAllProvidersEnabled_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Uri"] = "bolt://localhost:7687",
            })
            .Build();

        Action act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Exactly one document projection provider must be enabled*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_WhenLegacyProviderConfigured_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Provider"] = "InMemory",
            })
            .Build();

        Action act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Legacy provider single-selection options are no longer supported*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_WhenPolicyDeniesInMemoryRelationFactStore_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Policies:DenyInMemoryGraphFactStore"] = "true",
            })
            .Build();

        Action act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*InMemory graph provider is not allowed*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_WhenProductionAndInMemoryDocumentEnabled_ShouldThrow()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Policies:Environment"] = "Production",
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "false",
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Uri"] = "bolt://localhost:7687",
            })
            .Build();

        Action act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*InMemory document provider is not allowed*");
    }
}
