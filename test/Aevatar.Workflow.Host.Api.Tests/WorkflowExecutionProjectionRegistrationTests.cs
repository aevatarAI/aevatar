using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No document projection store providers are registered*");
    }

    [Fact]
    public async Task AddWorkflowExecutionProjectionCQRS_ShouldResolveFanoutStores()
    {
        var services = new ServiceCollection();
        RegisterInMemoryProviders(services);
        services.AddWorkflowExecutionProjectionCQRS();

        await using var provider = services.BuildServiceProvider();
        var documentStore = provider.GetRequiredService<IDocumentProjectionStore<WorkflowExecutionReport, string>>();
        var relationStore = provider.GetRequiredService<IProjectionGraphStore>();
        var graphStore = provider.GetRequiredService<IProjectionGraphMaterializer<WorkflowExecutionReport>>();
        var router = provider.GetRequiredService<IProjectionMaterializationRouter<WorkflowExecutionReport, string>>();

        documentStore.Should().BeOfType<ProjectionDocumentStoreFanout<WorkflowExecutionReport, string>>();
        relationStore.Should().BeOfType<ProjectionGraphStoreFanout>();
        graphStore.Should().BeOfType<ProjectionGraphMaterializer<WorkflowExecutionReport>>();
        router.Should().NotBeNull();

        Func<Task> act = () => StartHostedServicesAsync(provider);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_WhenGraphProviderMissing_ShouldThrowOnGraphStoreResolution()
    {
        var services = new ServiceCollection();
        RegisterElasticsearchDocumentProvider(services);
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        Action act = () => provider.GetRequiredService<IProjectionGraphStore>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No graph projection store providers are registered*");
    }

    private static void RegisterInMemoryProviders(IServiceCollection services)
    {
        services.AddInMemoryDocumentStoreRegistration<WorkflowExecutionReport, string>(
            keySelector: report => report.RootActorId,
            isPrimaryQueryStore: true,
            keyFormatter: key => key,
            listSortSelector: report => report.CreatedAt,
            listTakeMax: 200);
        services.AddInMemoryGraphStoreRegistration(isPrimaryQueryStore: true);
    }

    private static void RegisterElasticsearchDocumentProvider(IServiceCollection services)
    {
        services.AddElasticsearchDocumentStoreRegistration<WorkflowExecutionReport, string>(
            optionsFactory: _ => new ElasticsearchProjectionReadModelStoreOptions
            {
                Endpoints = ["http://localhost:9200"],
            },
            metadataFactory: sp =>
            {
                var metadataResolver = sp.GetRequiredService<IProjectionDocumentMetadataResolver>();
                return metadataResolver.Resolve<WorkflowExecutionReport>();
            },
            keySelector: report => report.RootActorId,
            isPrimaryQueryStore: true,
            keyFormatter: key => key);
    }

    private static async Task StartHostedServicesAsync(IServiceProvider provider)
    {
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);
    }
}
