using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowExecutionProjectionRegistrationTests
{
    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_WhenNoProvidersRegistered_ShouldFailFast()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        Func<Task> act = () => StartHostedServicesAsync(provider);

        await act.Should().ThrowAsync<ProjectionProviderSelectionException>()
            .WithMessage("*No provider registrations were found*");
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_ShouldResolveInMemoryDocumentAndGraphStores()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProviders(services);
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        var documentStore = provider.GetRequiredService<IDocumentProjectionStore<WorkflowExecutionReport, string>>();
        var readModelStore = provider.GetRequiredService<IDocumentProjectionStore<WorkflowExecutionReport, string>>();
        var relationStore = provider.GetRequiredService<IProjectionGraphStore>();
        var graphStore = provider.GetRequiredService<IProjectionGraphMaterializer<WorkflowExecutionReport>>();
        var router = provider.GetRequiredService<IProjectionMaterializationRouter<WorkflowExecutionReport, string>>();

        documentStore.Should().BeOfType<InMemoryProjectionReadModelStore<WorkflowExecutionReport, string>>();
        readModelStore.Should().BeOfType<InMemoryProjectionReadModelStore<WorkflowExecutionReport, string>>();
        relationStore.Should().BeOfType<InMemoryProjectionGraphStore>();
        graphStore.Should().BeOfType<ProjectionGraphMaterializer<WorkflowExecutionReport>>();
        router.Should().NotBeNull();

        Func<Task> act = () => StartHostedServicesAsync(provider);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WhenDocumentElasticsearchAndGraphInMemoryConfigured_ShouldResolveSplitProviders()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProviders(services);
        RegisterElasticsearchDocumentProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
        {
            options.DocumentProvider = ProjectionProviderNames.Elasticsearch;
            options.GraphProvider = ProjectionProviderNames.InMemory;
        });
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        var readModelStore = provider.GetRequiredService<IDocumentProjectionStore<WorkflowExecutionReport, string>>();
        var relationStore = provider.GetRequiredService<IProjectionGraphStore>();

        readModelStore.Should().BeOfType<ElasticsearchProjectionReadModelStore<WorkflowExecutionReport, string>>();
        relationStore.Should().BeOfType<InMemoryProjectionGraphStore>();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WhenGraphProviderMissing_ShouldThrowOnGraphStoreResolution()
    {
        var services = new ServiceCollection();
        RegisterElasticsearchDocumentProvider(services);
        ConfigureStoreSelectionOptions(services, options =>
        {
            options.DocumentProvider = ProjectionProviderNames.Elasticsearch;
            options.GraphProvider = ProjectionProviderNames.Elasticsearch;
        });
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        Action act = () => provider.GetRequiredService<IProjectionGraphStore>();

        act.Should().Throw<ProjectionProviderSelectionException>()
            .WithMessage("*No relation store provider registrations were found*");
    }

    private static void RegisterInMemoryProviders(IServiceCollection services)
    {
        services.AddInMemoryDocumentStoreRegistration<WorkflowExecutionReport, string>(
            keySelector: report => report.RootActorId,
            keyFormatter: key => key,
            listSortSelector: report => report.CreatedAt,
            listTakeMax: 200);
        services.AddInMemoryGraphStoreRegistration();
    }

    private static void RegisterElasticsearchDocumentProvider(IServiceCollection services)
    {
        services.AddElasticsearchDocumentStoreRegistration<WorkflowExecutionReport, string>(
            optionsFactory: _ => new ElasticsearchProjectionReadModelStoreOptions
            {
                Endpoints = ["http://localhost:9200"],
            },
            indexScopeFactory: sp =>
            {
                var metadataResolver = sp.GetRequiredService<IProjectionDocumentMetadataResolver>();
                return metadataResolver.Resolve<WorkflowExecutionReport>().IndexName;
            },
            keySelector: report => report.RootActorId,
            keyFormatter: key => key);
    }

    private static void ConfigureStoreSelectionOptions(
        IServiceCollection services,
        Action<ProjectionStoreRuntimeOptions> configure)
    {
        var options = new ProjectionStoreRuntimeOptions();
        configure(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.Replace(ServiceDescriptor.Singleton<IProjectionStoreSelectionRuntimeOptions>(sp =>
            sp.GetRequiredService<ProjectionStoreRuntimeOptions>()));
    }

    private static async Task StartHostedServicesAsync(IServiceProvider provider)
    {
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);
    }
}
