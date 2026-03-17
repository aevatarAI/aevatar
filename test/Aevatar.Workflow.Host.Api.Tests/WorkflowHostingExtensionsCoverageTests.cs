using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Hosting;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Extensions.Maker;
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
    public void AddAevatarPlatform_ShouldValidateBuilder()
    {
        Action act = () => AevatarPlatformHostBuilderExtensions.AddAevatarPlatform(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task AddAevatarPlatform_ShouldRegisterWorkflowScriptingAiAndMakerBundles()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.AddAevatarPlatform(options =>
        {
            options.EnableMakerExtensions = true;
            options.ConfigureAIFeatures = aiOptions =>
            {
                aiOptions.EnableMCPTools = false;
                aiOptions.EnableSkills = false;
                aiOptions.ApiKey = "demo-key";
                aiOptions.DefaultProvider = "openai";
            };
        });

        builder.Services.Any(x => x.ServiceType == typeof(ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>)).Should().BeTrue();
        builder.Services.Any(x => x.ServiceType == typeof(ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>)).Should().BeTrue();
        builder.Services.Any(x => x.ServiceType == typeof(IWorkflowRunActorPort)).Should().BeTrue();
        builder.Services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>)).Should().BeTrue();
        builder.Services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowActorBindingDocument, string>)).Should().BeTrue();
        builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .Should()
            .Contain(x => x.Name == "workflow-bundle")
            .And.Contain(x => x.Name == "scripting-bundle");

        await using var provider = builder.Services.BuildServiceProvider();
        provider.GetService<ILLMProviderFactory>().Should().NotBeNull();
        provider.GetService<IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>>().Should().NotBeNull();
        provider.GetService<IProjectionDocumentReader<WorkflowActorBindingDocument, string>>().Should().NotBeNull();
        provider.GetServices<IWorkflowModulePack>().Should().ContainSingle(x => x is MakerModulePack);

        var toolSources = provider.GetServices<IAgentToolSource>().ToList();
        toolSources.Should().NotContain(x => x is MCPAgentToolSource);
        toolSources.Should().NotContain(x => x is SkillsAgentToolSource);
    }

    [Fact]
    public void AddAevatarPlatform_WhenScriptingDisabled_ShouldNotRegisterScriptingBundle()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.AddAevatarPlatform(options =>
        {
            options.EnableScriptingCapability = false;
        });

        builder.Services
            .Where(x => x.ServiceType == typeof(AevatarCapabilityRegistration))
            .Select(x => x.ImplementationInstance)
            .OfType<AevatarCapabilityRegistration>()
            .Should()
            .NotContain(x => x.Name == "scripting-bundle");
    }

    [Fact]
    public void AddAevatarPlatform_WhenMakerEnabledWithoutWorkflow_ShouldThrow()
    {
        var builder = WebApplication.CreateBuilder();

        var act = () => builder.AddAevatarPlatform(options =>
        {
            options.EnableWorkflowCapability = false;
            options.EnableMakerExtensions = true;
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Maker extensions require workflow capability*");
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

        var act = () => provider.GetRequiredService<IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>>();

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
    public void AddWorkflowProjectionReadModelProviders_ShouldUseAspNetCoreEnvironmentVariableForProductionPolicy()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspnet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");

        try
        {
            var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*InMemory document provider is not allowed*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnet);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspnet);
        }
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldInferElasticsearchProviderFromEndpoints()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            })
            .Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);

        services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>))
            .Should()
            .BeTrue();
        services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowActorBindingDocument, string>))
            .Should()
            .BeTrue();
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldInferNeo4jProviderFromUri()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:InMemory:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Uri"] = "bolt://localhost:7687",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "false",
            })
            .Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);

        services.Any(x => x.ServiceType == typeof(IProjectionGraphStore))
            .Should()
            .BeTrue();
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRejectInMemoryDocumentProviderWhenDeniedByPolicy()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Policies:DenyInMemoryDocumentReadStore"] = "true",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
            })
            .Build();

        var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*InMemory document provider is not allowed*");
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldBeIdempotent()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);
        var afterFirstRegistration = services.Count;
        services.AddWorkflowProjectionReadModelProviders(configuration);

        services.Count.Should().Be(afterFirstRegistration);
    }

    [Fact]
    public async Task AddWorkflowProjectionReadModelProviders_ShouldResolveWorkflowActorBindingDocumentStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>>()
            .Should()
            .NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<WorkflowActorBindingDocument, string>>()
            .Should()
            .NotBeNull();
    }
}
