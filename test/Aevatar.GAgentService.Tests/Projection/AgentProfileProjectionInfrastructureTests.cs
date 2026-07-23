using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Projection.AgentProfiles;
using Aevatar.GAgentService.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class AgentProfileProjectionInfrastructureTests
{
    [Fact]
    public void MetadataProviders_ShouldExposeExactThreeDocumentIndexes()
    {
        new AgentProfileNamespaceCatalogDocumentMetadataProvider().Metadata.IndexName
            .Should().Be("agent-profile-namespaces");
        new AgentProfileOwnerDocumentMetadataProvider().Metadata.IndexName
            .Should().Be("agent-profile-management");
        new AgentProfileExecutionDocumentMetadataProvider().Metadata.IndexName
            .Should().Be("agent-profile-execution");

        var profileReadModels = typeof(AgentProfileOwnerDocument).Assembly.GetTypes()
            .Where(type =>
                string.Equals(
                    type.Namespace,
                    "Aevatar.GAgentService.Projection.AgentProfiles",
                    StringComparison.Ordinal) &&
                typeof(IProjectionReadModel).IsAssignableFrom(type))
            .ToArray();
        profileReadModels.Should().BeEquivalentTo(
            new[]
            {
                typeof(AgentProfileNamespaceCatalogDocument),
                typeof(AgentProfileOwnerDocument),
                typeof(AgentProfileExecutionDocument),
            });
    }

    [Fact]
    public void ActivationProvider_ShouldCreateOneNamespacePlanAndTwoProfileFanOutPlans()
    {
        var provider = new ServiceCommittedStateProjectionActivationPlanProvider();

        var namespacePlans = provider.GetPlans(BuildContext(
            typeof(AgentProfileNamespaceGAgent),
            AgentProfileActorIds.Namespace)).ToArray();
        var profilePlans = provider.GetPlans(BuildContext(
            typeof(AgentProfileGAgent),
            "profile-actor-alpha")).ToArray();

        namespacePlans.Should().ContainSingle();
        namespacePlans[0].LeaseType.Should().Be(
            typeof(ServiceProjectionRuntimeLease<AgentProfileNamespaceCurrentStateProjectionContext>));
        namespacePlans[0].StartRequest.RootActorId.Should().Be(AgentProfileActorIds.Namespace);
        namespacePlans[0].StartRequest.ProjectionKind.Should().Be("agent-profile-namespaces");
        namespacePlans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
        namespacePlans[0].StartRequest.SessionId.Should().BeEmpty();

        profilePlans.Should().HaveCount(2);
        profilePlans.Select(plan => plan.LeaseType).Should().Equal(
            typeof(ServiceProjectionRuntimeLease<AgentProfileOwnerCurrentStateProjectionContext>),
            typeof(ServiceProjectionRuntimeLease<AgentProfileExecutionCurrentStateProjectionContext>));
        profilePlans.Select(plan => plan.StartRequest.ProjectionKind).Should().Equal(
            "agent-profile-management",
            "agent-profile-execution");
        profilePlans.Should().OnlyContain(plan =>
            plan.StartRequest.RootActorId == "profile-actor-alpha" &&
            plan.StartRequest.Mode == ProjectionRuntimeMode.DurableMaterialization &&
            plan.StartRequest.SessionId == string.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectionComposition_ShouldRegisterAllProfileServicesOnceForSelectedProvider(
        bool elasticsearch)
    {
        var services = new ServiceCollection();
        var configuration = BuildProviderConfiguration(elasticsearch);

        Aevatar.GAgentService.Projection.DependencyInjection.ServiceCollectionExtensions
            .AddGAgentServiceProjection(services);
        Aevatar.GAgentService.Projection.DependencyInjection.ServiceCollectionExtensions
            .AddGAgentServiceProjection(services);
        Aevatar.GAgentService.Hosting.DependencyInjection.ServiceCollectionExtensions
            .AddGAgentServiceProjectionReadModelProviders(services, configuration);
        var countAfterFirstProviderRegistration = services.Count;
        Aevatar.GAgentService.Hosting.DependencyInjection.ServiceCollectionExtensions
            .AddGAgentServiceProjectionReadModelProviders(services, configuration);

        services.Count.Should().Be(countAfterFirstProviderRegistration);
        AssertContextAndMaterializerRegistration<
            AgentProfileNamespaceCurrentStateProjectionContext,
            AgentProfileNamespaceCurrentStateProjector>(services);
        AssertContextAndMaterializerRegistration<
            AgentProfileOwnerCurrentStateProjectionContext,
            AgentProfileOwnerCurrentStateProjector>(services);
        AssertContextAndMaterializerRegistration<
            AgentProfileExecutionCurrentStateProjectionContext,
            AgentProfileExecutionCurrentStateProjector>(services);
        AssertSingletonRegistration<
            IProjectionDocumentMetadataProvider<AgentProfileNamespaceCatalogDocument>,
            AgentProfileNamespaceCatalogDocumentMetadataProvider>(services);
        AssertSingletonRegistration<
            IProjectionDocumentMetadataProvider<AgentProfileOwnerDocument>,
            AgentProfileOwnerDocumentMetadataProvider>(services);
        AssertSingletonRegistration<
            IProjectionDocumentMetadataProvider<AgentProfileExecutionDocument>,
            AgentProfileExecutionDocumentMetadataProvider>(services);
        AssertSingletonRegistration<
            IAgentProfileNamespaceQueryPort,
            ProjectionAgentProfileNamespaceQueryPort>(services);
        AssertSingletonRegistration<
            IAgentProfileManagementQueryPort,
            ProjectionAgentProfileManagementQueryPort>(services);
        AssertSingletonRegistration<
            IAgentProfileExecutionSnapshotQueryPort,
            ProjectionAgentProfileExecutionSnapshotQueryPort>(services);
        AssertProviderRegistration<AgentProfileNamespaceCatalogDocument>(services, elasticsearch);
        AssertProviderRegistration<AgentProfileOwnerDocument>(services, elasticsearch);
        AssertProviderRegistration<AgentProfileExecutionDocument>(services, elasticsearch);
    }

    private static void AssertContextAndMaterializerRegistration<TContext, TProjector>(
        IServiceCollection services)
        where TContext : class, IProjectionMaterializationContext
    {
        services.Count(descriptor =>
                descriptor.ServiceType == typeof(Func<ProjectionRuntimeScopeKey, TContext>))
            .Should().Be(1);
        services.Count(descriptor => descriptor.ServiceType == typeof(TProjector))
            .Should().Be(1);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(ICurrentStateProjectionMaterializer<TContext>) &&
            IsObservedCurrentStateMaterializerFor<TProjector>(descriptor.ImplementationType));
    }

    private static void AssertSingletonRegistration<TService, TImplementation>(
        IServiceCollection services)
    {
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(TService) &&
            descriptor.ImplementationType == typeof(TImplementation) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    private static void AssertProviderRegistration<TDocument>(
        IServiceCollection services,
        bool elasticsearch)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        var expectedStore = elasticsearch
            ? typeof(ElasticsearchProjectionDocumentStore<TDocument, string>)
            : typeof(InMemoryProjectionDocumentStore<TDocument, string>);
        var rejectedStore = elasticsearch
            ? typeof(InMemoryProjectionDocumentStore<TDocument, string>)
            : typeof(ElasticsearchProjectionDocumentStore<TDocument, string>);

        services.Count(descriptor => descriptor.ServiceType == expectedStore).Should().Be(1);
        services.Should().NotContain(descriptor => descriptor.ServiceType == rejectedStore);
        services.Count(descriptor =>
                descriptor.ServiceType == typeof(IProjectionDocumentReader<TDocument, string>))
            .Should().Be(1);
        services.Count(descriptor =>
                descriptor.ServiceType == typeof(IProjectionDocumentWriter<TDocument>))
            .Should().Be(1);
    }

    private static bool IsObservedCurrentStateMaterializerFor<TProjector>(System.Type? type) =>
        type?.IsGenericType == true &&
        type.Name.StartsWith("ObservedCurrentStateProjectionMaterializer`", StringComparison.Ordinal) &&
        type.GenericTypeArguments.Length == 2 &&
        type.GenericTypeArguments[1] == typeof(TProjector);

    private static IConfiguration BuildProviderConfiguration(bool elasticsearch)
    {
        var values = new Dictionary<string, string?>
        {
            ["Projection:Policies:Environment"] = "Development",
            ["Projection:Document:Providers:Elasticsearch:Enabled"] =
                elasticsearch ? "true" : "false",
            ["Projection:Document:Providers:InMemory:Enabled"] =
                elasticsearch ? "false" : "true",
        };
        if (elasticsearch)
        {
            values["Projection:Document:Providers:Elasticsearch:Endpoints:0"] =
                "http://localhost:9200";
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static CommittedStatePublicationContext BuildContext(System.Type actorType, string actorId) =>
        new()
        {
            ActorId = actorId,
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = actorId,
                    EventId = "evt-activation",
                    Version = 1,
                    EventData = Any.Pack(new StringValue { Value = "committed" }),
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };
}
