using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Application.Studio.ProjectionRecovery;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioHostingNyxIdAuthorizationCatalogCompositionTests
{
    [Fact]
    public void AddStudioHostingCore_ShouldComposeAndResolveNyxIdAuthorizationCatalogGraph()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        services.AddAevatarRuntime();

        services.AddStudioHostingCore(configuration);

        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(INyxIdAuthorizationCatalogCommandPort) &&
            descriptor.ImplementationType == typeof(NyxIdAuthorizationCatalogCommandPort));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(INyxIdAuthorizationCatalogRefreshPort) &&
            descriptor.ImplementationType == typeof(NyxIdAuthorizationCatalogRefreshPort));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(IScheduledInvocationAuthorizationPlanner) &&
            descriptor.ImplementationType == typeof(ScheduledInvocationAuthorizationPlanner));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(IScheduledInvocationAuthorizationRevalidator) &&
            descriptor.ImplementationType == typeof(ScheduledInvocationAuthorizationRevalidator));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(INyxIdApiClientFactory));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(INyxIdAuthorizationCatalogQueryPort) &&
            descriptor.ImplementationType == typeof(ProjectionNyxIdAuthorizationCatalogQueryPort));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(
                INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort) &&
            descriptor.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(
                INyxIdAuthorizationCatalogRefreshObservationProjectionPort) &&
            descriptor.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationProjectionPort));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionSessionEventCodec<NyxIdAuthorizationCatalogRefreshCommittedOutcome>) &&
            descriptor.ImplementationType == typeof(
                NyxIdAuthorizationCatalogRefreshObservationSessionEventCodec));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome>));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(NyxIdAuthorizationCatalogCurrentStateProjector));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionMaterializer<NyxIdAuthorizationCatalogProjectionContext>));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(
                IProjectionDocumentReader<NyxIdAuthorizationCatalogDocument, string>));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(NyxIdAuthorizationCatalogGAgent));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(IExternalWorkflowCapabilitySource) &&
            descriptor.ImplementationType == typeof(NyxIdExternalWorkflowCapabilitySource));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(IStudioWorkspaceProjectionRepublishPort));
        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType == typeof(IStudioWorkspaceVersionRegressionStorePort));
        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType == typeof(IStudioWorkspaceVersionRegressionRepairService));
        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType == typeof(
                IElasticsearchProjectionDocumentRepairStore<
                    StudioWorkspaceCurrentStateDocument,
                    string>));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<INyxIdAuthorizationCatalogCommandPort>()
            .Should().BeOfType<NyxIdAuthorizationCatalogCommandPort>();
        provider.GetRequiredService<INyxIdAuthorizationCatalogRefreshPort>()
            .Should().BeOfType<NyxIdAuthorizationCatalogRefreshPort>();
        provider.GetRequiredService<IScheduledInvocationAuthorizationPlanner>()
            .Should().BeOfType<ScheduledInvocationAuthorizationPlanner>();
        provider.GetRequiredService<IScheduledInvocationAuthorizationRevalidator>()
            .Should().BeOfType<ScheduledInvocationAuthorizationRevalidator>();
        provider.GetRequiredService<INyxIdApiClientFactory>().Should().NotBeNull();
        provider.GetRequiredService<INyxIdAuthorizationCatalogQueryPort>()
            .Should().BeOfType<ProjectionNyxIdAuthorizationCatalogQueryPort>();
        provider.GetRequiredService<INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort>()
            .Should().BeOfType<NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort>();
        provider.GetRequiredService<INyxIdAuthorizationCatalogRefreshObservationProjectionPort>()
            .Should().BeOfType<NyxIdAuthorizationCatalogRefreshObservationProjectionPort>();
        provider.GetRequiredService<NyxIdAuthorizationCatalogCurrentStateProjector>()
            .Should().NotBeNull();
        provider.GetRequiredService<IProjectionDocumentReader<NyxIdAuthorizationCatalogDocument, string>>()
            .Should().NotBeNull();

        var registry = provider.GetRequiredService<IAgentKindRegistry>();
        registry.TryGetKindForAgentType(typeof(NyxIdAuthorizationCatalogGAgent), out var kind)
            .Should().BeTrue();
        kind.Should().Be("gagent.service.nyxid-authorization-catalog");
    }

    [Fact]
    public void AddStudioHostingCore_WithElasticsearch_ShouldComposeWorkspaceRepairOnlyForWorkspaceDocument()
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

        services.AddStudioHostingCore(configuration);

        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(
                IElasticsearchProjectionDocumentRepairStore<
                    StudioWorkspaceCurrentStateDocument,
                    string>));
        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType == typeof(
                IElasticsearchProjectionDocumentRepairStore<
                    StudioTeamCurrentStateDocument,
                    string>));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(IStudioWorkspaceVersionRegressionStorePort));
        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(IStudioWorkspaceVersionRegressionRepairService));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStudioWorkspaceVersionRegressionRepairService>()
            .Should().BeOfType<StudioWorkspaceVersionRegressionRepairService>();
        provider.GetRequiredService<
                IElasticsearchProjectionDocumentRepairStore<
                    StudioWorkspaceCurrentStateDocument,
                    string>>()
            .Should().NotBeNull();
    }
}
