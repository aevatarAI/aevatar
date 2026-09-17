using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Extensions.Maker;
using Aevatar.Workflow.Extensions.Schedules;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class WorkflowHostingExtensionsCoverageTests
{
    [Fact]
    public void AddAevatarPlatform_ShouldValidateBuilder()
    {
        Action act = () => AevatarPlatformHostBuilderExtensions.AddAevatarPlatform(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WorkflowPlatformServices_ShouldRegisterWorkflowAndMakerServices()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);
        services.AddWorkflowCapability(configuration);
        services.AddWorkflowMakerExtensions();

        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowChatRunInteractionPort));
        services.Should().NotContain(x =>
            x.ServiceType == typeof(ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>));
        services.Should().Contain(x => x.ServiceType == typeof(ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowRunProvisioningPort));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowDefinitionProvisioningPort));
        services.Should().Contain(x => x.ServiceType == typeof(IWorkflowDefinitionParser));
        services.Should().Contain(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>));
        services.Should().Contain(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowActorBindingDocument, string>));

        await using var provider = services.BuildServiceProvider();
        provider.GetService<IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>>().Should().NotBeNull();
        provider.GetService<IProjectionDocumentReader<WorkflowActorBindingDocument, string>>().Should().NotBeNull();
        provider.GetServices<IWorkflowModulePack>().Should().ContainSingle(x => x is MakerModulePack);
        var schedulePack = provider.GetServices<IWorkflowModulePack>()
            .Should()
            .ContainSingle(x => x is WorkflowScheduleModulePack)
            .Which;
        schedulePack.Modules.Should().ContainSingle()
            .Which.Names.Should().BeEquivalentTo(["self_reschedule", "schedule_workflow"]);
    }

    [Fact]
    public void AddAevatarPlatform_WhenMakerEnabledWithoutWorkflow_ShouldThrow()
    {
        var options = new AevatarPlatformCompositionOptions
        {
            EnableAIFeatures = false,
            EnableWorkflowCapability = false,
            EnableMakerExtensions = true,
        };

        var act = () => InvokeValidateOptions(options);

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
            .WithMessage("*Only one graph projection provider can be enabled*");
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
    public void AddWorkflowProjectionReadModelProviders_ShouldFillMissingReadersWhenPartialRegistrationExists()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddInMemoryDocumentProjectionStore<WorkflowExecutionCurrentStateDocument, string>(
            keySelector: static document => document.RootActorId,
            keyFormatter: static key => key,
            defaultSortSelector: static document => document.UpdatedAt,
            queryTakeMax: 200);

        services.AddWorkflowProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>>().Should().NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<WorkflowActorBindingDocument, string>>().Should().NotBeNull();
        services.Count(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string>)).Should().Be(1);
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRejectPartialRegistrationFromDifferentProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
                ["Projection:Graph:Providers:InMemory:Enabled"] = "true",
                ["Projection:Graph:Providers:Neo4j:Enabled"] = "false",
            })
            .Build();

        services.AddInMemoryDocumentProjectionStore<WorkflowExecutionCurrentStateDocument, string>(
            keySelector: static document => document.RootActorId,
            keyFormatter: static key => key,
            defaultSortSelector: static document => document.UpdatedAt,
            queryTakeMax: 200);

        var act = () => services.AddWorkflowProjectionReadModelProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WorkflowExecutionCurrentStateDocument*different provider*");
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

    private static void InvokeValidateOptions(AevatarPlatformCompositionOptions options)
    {
        var method = typeof(AevatarPlatformHostBuilderExtensions)
            .GetMethod("ValidateOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull();
        try
        {
            method!.Invoke(null, [options]);
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
