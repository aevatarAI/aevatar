using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Projection.Runtime;
using Aevatar.Workflow.Extensions.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// The fleet capability authority read model gates every schema adoption; without a document
/// store binding on the active provider its projection fails and admission stays closed.
/// </summary>
public sealed class RuntimeFleetCapabilityAuthorityDocumentProviderRegistrationTests
{
    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRegisterInMemoryFleetAuthorityDocumentStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionDocumentReader<RuntimeFleetCapabilityAuthorityCurrentStateDocument, string>>()
            .Should()
            .BeOfType<InMemoryProjectionDocumentStore<RuntimeFleetCapabilityAuthorityCurrentStateDocument, string>>();
        provider.GetRequiredService<IProjectionDocumentWriter<RuntimeFleetCapabilityAuthorityCurrentStateDocument>>()
            .Should()
            .BeOfType<InMemoryProjectionDocumentStore<RuntimeFleetCapabilityAuthorityCurrentStateDocument, string>>();
    }

    [Fact]
    public void AddWorkflowProjectionReadModelProviders_ShouldRegisterElasticsearchFleetAuthorityDocumentStore()
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
            x.ServiceType == typeof(ElasticsearchProjectionDocumentStore<RuntimeFleetCapabilityAuthorityCurrentStateDocument, string>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentReader<RuntimeFleetCapabilityAuthorityCurrentStateDocument, string>));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentWriter<RuntimeFleetCapabilityAuthorityCurrentStateDocument>));
    }
}
