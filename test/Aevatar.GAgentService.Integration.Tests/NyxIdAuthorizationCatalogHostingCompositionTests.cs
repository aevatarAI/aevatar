using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class NyxIdAuthorizationCatalogHostingCompositionTests
{
    [Fact]
    public void NyxIdCatalogHosting_WithElasticsearch_ShouldResolveVersionRegressionRepair()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
                ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] =
                    "http://localhost:9200",
                ["Projection:Document:Providers:InMemory:Enabled"] = "false",
            })
            .Build();
        services.AddAevatarRuntime();

        services.AddNyxIdAuthorizationCatalogHosting(configuration);

        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(
                IElasticsearchProjectionDocumentRepairStore<
                    NyxIdAuthorizationCatalogDocument,
                    string>));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType ==
            typeof(INyxIdAuthorizationCatalogVersionRegressionStorePort));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType ==
            typeof(INyxIdAuthorizationCatalogVersionRegressionRepairService));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<INyxIdAuthorizationCatalogVersionRegressionRepairService>()
            .Should().BeOfType<NyxIdAuthorizationCatalogVersionRegressionRepairService>();
        provider.GetRequiredService<
                IElasticsearchProjectionDocumentRepairStore<
                    NyxIdAuthorizationCatalogDocument,
                    string>>()
            .Should().NotBeNull();
    }

    [Fact]
    public void NyxIdCatalogHosting_WithInMemory_ShouldNotExposeVersionRegressionRepair()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        services.AddAevatarRuntime();

        services.AddNyxIdAuthorizationCatalogHosting(configuration);

        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType == typeof(
                IElasticsearchProjectionDocumentRepairStore<
                    NyxIdAuthorizationCatalogDocument,
                    string>));
        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType ==
            typeof(INyxIdAuthorizationCatalogVersionRegressionStorePort));
        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType ==
            typeof(INyxIdAuthorizationCatalogVersionRegressionRepairService));

        using var provider = services.BuildServiceProvider();
        provider.GetService<INyxIdAuthorizationCatalogVersionRegressionRepairService>()
            .Should().BeNull();
    }

    [Fact]
    public void NyxIdCatalogHosting_WithInMemoryAndAccidentalRepairStore_ShouldNotExposeRepairService()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        services.AddAevatarRuntime();
        services.AddSingleton<
            IElasticsearchProjectionDocumentRepairStore<
                NyxIdAuthorizationCatalogDocument,
                string>,
            PreRegisteredRepairStore>();

        services.AddNyxIdAuthorizationCatalogHosting(configuration);

        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType ==
            typeof(INyxIdAuthorizationCatalogVersionRegressionStorePort));
        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType ==
            typeof(INyxIdAuthorizationCatalogVersionRegressionRepairService));

        using var provider = services.BuildServiceProvider();
        provider.GetService<INyxIdAuthorizationCatalogVersionRegressionRepairService>()
            .Should().BeNull();
    }

    [Fact]
    public void FullAndScheduledCapabilities_WhenRepeated_ShouldRegisterSingleNyxIdApiClient()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddGAgentServiceCapability(configuration);
        services.AddScheduledDispatchCapability(configuration);
        services.AddGAgentServiceCapability(configuration);

        services.Count(static descriptor => descriptor.ServiceType == typeof(NyxIdApiClient))
            .Should().Be(1);
        services.Count(static descriptor => descriptor.ServiceType == typeof(INyxIdApiClientFactory))
            .Should().Be(1);
    }

    [Fact]
    public void NyxIdCatalogHosting_WhenRepeatedAroundFullCapabilities_ShouldKeepDescriptorsSingle()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddNyxIdAuthorizationCatalogHosting(configuration);
        services.AddGAgentServiceCapability(configuration);
        services.AddNyxIdAuthorizationCatalogHosting(configuration);
        services.AddScheduledDispatchCapability(configuration);

        services.Count(static descriptor =>
                descriptor.ServiceType == typeof(NyxIdAuthorizationCatalogHostingRegistrationsMarker))
            .Should().Be(1);
        services.Count(static descriptor =>
                descriptor.ServiceType == typeof(INyxIdAuthorizationCatalogCommandPort) &&
                descriptor.ImplementationType == typeof(NyxIdAuthorizationCatalogCommandPort))
            .Should().Be(1);
        services.Count(static descriptor =>
                descriptor.ServiceType == typeof(INyxIdAuthorizationCatalogRefreshPort) &&
                descriptor.ImplementationType == typeof(NyxIdAuthorizationCatalogRefreshPort))
            .Should().Be(1);
        services.Count(static descriptor =>
                descriptor.ServiceType == typeof(INyxIdAuthorizationCatalogQueryPort) &&
                descriptor.ImplementationType == typeof(ProjectionNyxIdAuthorizationCatalogQueryPort))
            .Should().Be(1);
        services.Count(static descriptor =>
                descriptor.ServiceType == typeof(
                    INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort) &&
                descriptor.ImplementationType == typeof(
                    NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort))
            .Should().Be(1);
        services.Count(static descriptor =>
                descriptor.ServiceType == typeof(
                    INyxIdAuthorizationCatalogRefreshObservationProjectionPort) &&
                descriptor.ImplementationType == typeof(
                    NyxIdAuthorizationCatalogRefreshObservationProjectionPort))
            .Should().Be(1);
        services.Count(static descriptor =>
                descriptor.ServiceType == typeof(NyxIdAuthorizationCatalogCurrentStateProjector))
            .Should().Be(1);
        services.Count(static descriptor =>
                descriptor.ServiceType == typeof(
                    IProjectionMaterializer<NyxIdAuthorizationCatalogProjectionContext>))
            .Should().Be(1);
        services.Count(static descriptor => descriptor.ServiceType == typeof(NyxIdApiClient))
            .Should().Be(1);
        services.Count(static descriptor => descriptor.ServiceType == typeof(INyxIdApiClientFactory))
            .Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NyxIdCatalogHostingAndTools_InEitherOrder_ShouldKeepOneOptionsAndCapabilitySource(
        bool catalogFirst)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        if (catalogFirst)
        {
            services.AddNyxIdAuthorizationCatalogHosting(configuration);
            services.AddNyxIdTools(options => options.BaseUrl = "https://nyxid.invalid");
        }
        else
        {
            services.AddNyxIdTools(options => options.BaseUrl = "https://nyxid.invalid");
            services.AddNyxIdAuthorizationCatalogHosting(configuration);
        }

        services.Count(static descriptor => descriptor.ServiceType == typeof(NyxIdToolOptions))
            .Should().Be(1);
        services.Count(static descriptor =>
                descriptor.ServiceType == typeof(IExternalWorkflowCapabilitySource) &&
                descriptor.ImplementationType == typeof(NyxIdExternalWorkflowCapabilitySource))
            .Should().Be(1);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<NyxIdToolOptions>().BaseUrl.Should().Be("https://nyxid.invalid");
    }

    private sealed class PreRegisteredRepairStore
        : IElasticsearchProjectionDocumentRepairStore<
            NyxIdAuthorizationCatalogDocument,
            string>
    {
        public Task<ElasticsearchProjectionDocumentRepairLease<
            NyxIdAuthorizationCatalogDocument,
            string>?> InspectAsync(
            string key,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ElasticsearchProjectionDocumentRepairDeleteDisposition>
            DeleteIfUnchangedAsync(
                ElasticsearchProjectionDocumentRepairLease<
                    NyxIdAuthorizationCatalogDocument,
                    string> lease,
                CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
