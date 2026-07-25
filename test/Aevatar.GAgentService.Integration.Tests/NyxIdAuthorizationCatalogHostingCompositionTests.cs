using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class NyxIdAuthorizationCatalogHostingCompositionTests
{
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
}
