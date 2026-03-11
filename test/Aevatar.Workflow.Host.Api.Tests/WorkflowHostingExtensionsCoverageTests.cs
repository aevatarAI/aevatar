using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Hosting;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowHostingExtensionsCoverageTests
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
        }, includeScriptCapability: true);

        builder.Services.Any(x => x.ServiceType == typeof(IWorkflowRunInteractionService)).Should().BeTrue();
        builder.Services.Any(x => x.ServiceType == typeof(ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>)).Should().BeTrue();
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
    public void AddWorkflowCapabilityWithAIDefaults_WhenScriptCapabilityDisabled_ShouldNotRegisterScriptCapability()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.AddWorkflowCapabilityWithAIDefaults(includeScriptCapability: false);

        builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .Should()
            .NotContain(x => x.Name == "script");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRejectLegacySingleProviderOptions()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Provider"] = "elasticsearch",
            })
            .Build();

        var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Legacy provider single-selection options are no longer supported*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRejectInvalidBooleanFlags()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:InMemory:Enabled"] = "not-a-bool",
            })
            .Build();

        var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Invalid boolean value*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRejectMultipleEnabledProviders()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
            })
            .Build();

        var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Exactly one document projection provider must be enabled*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRejectMultipleEnabledGraphProviders()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Uri"] = "bolt://localhost:7687",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            })
            .Build();

        var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Exactly one graph projection provider must be enabled*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRejectInMemoryProvidersInProductionPolicy()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Policies:Environment"] = "Production",
            })
            .Build();

        var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*InMemory document provider is not allowed*");
    }

    [Fact]
    public async Task AddWorkflowProjectionReadModelProviders_ShouldRejectElasticsearchWithoutEndpoints()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
            })
            .Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);
        await using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IProjectionDocumentStore<WorkflowExecutionReport, string>>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Endpoints is empty*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRejectInMemoryGraphProviderWhenDeniedByPolicy()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
                ["Projection:Policies:DenyInMemoryGraphFactStore"] = "true",
            })
            .Build();

        var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*InMemory graph provider is not allowed*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldUseEnvironmentVariableForProductionPolicy()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var previous = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");

        try
        {
            var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*InMemory document provider is not allowed*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previous);
        }
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldBeIdempotent()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);
        services.AddWorkflowProjectionReadModelProviders(configuration);

        services.Count(x => x.ServiceType.Name.Contains("WorkflowProjectionProviderRegistrationsMarker", StringComparison.Ordinal))
            .Should()
            .Be(1);
    }
}
