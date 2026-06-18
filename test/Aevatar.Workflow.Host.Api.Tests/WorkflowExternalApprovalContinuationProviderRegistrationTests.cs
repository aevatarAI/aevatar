using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExternalApprovalContinuationProviderRegistrationTests
{
    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRegisterContinuationDocumentStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string>>()
            .Should()
            .BeOfType<InMemoryProjectionDocumentStore<WorkflowExternalApprovalContinuationDocument, string>>();
        provider.GetRequiredService<IProjectionDocumentWriter<WorkflowExternalApprovalContinuationDocument>>()
            .Should()
            .BeOfType<InMemoryProjectionDocumentStore<WorkflowExternalApprovalContinuationDocument, string>>();
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldFillMissingContinuationDocumentStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        RegisterAllInMemoryWorkflowStoresExceptContinuation(services);

        services.AddWorkflowProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string>>()
            .Should()
            .BeOfType<InMemoryProjectionDocumentStore<WorkflowExternalApprovalContinuationDocument, string>>();
        provider.GetRequiredService<IProjectionDocumentWriter<WorkflowExternalApprovalContinuationDocument>>()
            .Should()
            .BeOfType<InMemoryProjectionDocumentStore<WorkflowExternalApprovalContinuationDocument, string>>();
        services.Count(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string>))
            .Should()
            .Be(1);
        services.Count(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>))
            .Should()
            .Be(1);
        services.Count(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowActorBindingDocument, string>))
            .Should()
            .Be(1);
        services.Count(x => x.ServiceType == typeof(IProjectionDocumentReader<WorkflowCatalogCurrentStateDocument, string>))
            .Should()
            .Be(1);
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldInferElasticsearchContinuationDocumentStore()
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

        services.Should().Contain(x =>
            x.ServiceType == typeof(ElasticsearchProjectionDocumentStore<WorkflowExternalApprovalContinuationDocument, string>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentWriter<WorkflowExternalApprovalContinuationDocument>));
    }

    private static void RegisterAllInMemoryWorkflowStoresExceptContinuation(IServiceCollection services)
    {
        services.AddInMemoryDocumentProjectionStore<WorkflowExecutionCurrentStateDocument, string>(
            keySelector: static document => document.RootActorId,
            keyFormatter: static key => key,
            defaultSortSelector: static document => document.UpdatedAt,
            queryTakeMax: 200);
        services.AddInMemoryDocumentProjectionStore<WorkflowRunInsightReportDocument, string>(
            keySelector: static document => document.RootActorId,
            keyFormatter: static key => key,
            defaultSortSelector: static document => document.CreatedAt,
            queryTakeMax: 200);
        services.AddInMemoryDocumentProjectionStore<WorkflowActorBindingDocument, string>(
            keySelector: static document => document.Id,
            keyFormatter: static key => key,
            defaultSortSelector: static document => document.UpdatedAt,
            queryTakeMax: 200);
        services.AddInMemoryDocumentProjectionStore<WorkflowCatalogCurrentStateDocument, string>(
            keySelector: static document => document.Id,
            keyFormatter: static key => key,
            defaultSortSelector: static document => document.UpdatedAt,
            queryTakeMax: 200);
    }
}
