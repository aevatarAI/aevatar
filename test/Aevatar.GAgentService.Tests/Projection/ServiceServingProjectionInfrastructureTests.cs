using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceServingProjectionInfrastructureTests
{
    [Fact]
    public async Task ServingProjectionPorts_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var options = new ServiceProjectionOptions();
        var deploymentActivation = new RecordingProjectionActivationService<ServiceDeploymentCatalogProjectionContext>(
            (root, projectionName) => new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = root,
                ProjectionKind = projectionName,
            });
        var servingActivation = new RecordingProjectionActivationService<ServiceServingSetProjectionContext>(
            (root, projectionName) => new ServiceServingSetProjectionContext
            {
                RootActorId = root,
                ProjectionKind = projectionName,
            });
        var rolloutActivation = new RecordingProjectionActivationService<ServiceRolloutProjectionContext>(
            (root, projectionName) => new ServiceRolloutProjectionContext
            {
                RootActorId = root,
                ProjectionKind = projectionName,
            });
        var trafficActivation = new RecordingProjectionActivationService<ServiceTrafficViewProjectionContext>(
            (root, projectionName) => new ServiceTrafficViewProjectionContext
            {
                RootActorId = root,
                ProjectionKind = projectionName,
            });

        IServiceDeploymentCatalogProjectionPort deploymentPort = new ServiceDeploymentCatalogProjectionPort(
            options,
            deploymentActivation,
            new NoOpProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceDeploymentCatalogProjectionContext>>());
        IServiceServingSetProjectionPort servingPort = new ServiceServingSetProjectionPort(
            options,
            servingActivation,
            new NoOpProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceServingSetProjectionContext>>());
        IServiceRolloutProjectionPort rolloutPort = new ServiceRolloutProjectionPort(
            options,
            rolloutActivation,
            new NoOpProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceRolloutProjectionContext>>());
        IServiceTrafficViewProjectionPort trafficPort = new ServiceTrafficViewProjectionPort(
            options,
            trafficActivation,
            new NoOpProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceTrafficViewProjectionContext>>());

        await deploymentPort.EnsureProjectionAsync("");
        await deploymentPort.EnsureProjectionAsync("actor-deploy");
        await servingPort.EnsureProjectionAsync(" ");
        await servingPort.EnsureProjectionAsync("actor-serving");
        await rolloutPort.EnsureProjectionAsync("actor-rollout");
        await trafficPort.EnsureProjectionAsync("actor-traffic");

        deploymentActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-deploy", "service-deployments"));
        servingActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-serving", "service-serving"));
        rolloutActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-rollout", "service-rollouts"));
        trafficActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-traffic", "service-traffic"));
    }

    [Fact]
    public void ServingMetadataProviders_ShouldExposeStableIndexNames()
    {
        new ServiceDeploymentCatalogReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-deployments");
        new ServiceServingSetReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-serving");
        new ServiceRolloutReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-rollouts");
        new ServiceTrafficViewReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-traffic");
    }

    [Fact]
    public void AddGAgentServiceProjection_ShouldRegisterServingProjectionServices()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceDeploymentCatalogReadModel>) &&
            x.ImplementationType == typeof(ServiceDeploymentCatalogReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceServingSetReadModel>) &&
            x.ImplementationType == typeof(ServiceServingSetReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceRolloutReadModel>) &&
            x.ImplementationType == typeof(ServiceRolloutReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceTrafficViewReadModel>) &&
            x.ImplementationType == typeof(ServiceTrafficViewReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceDeploymentCatalogQueryReader) &&
            x.ImplementationType == typeof(ServiceDeploymentCatalogQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceServingSetQueryReader) &&
            x.ImplementationType == typeof(ServiceServingSetQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceRolloutQueryReader) &&
            x.ImplementationType == typeof(ServiceRolloutQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceTrafficViewQueryReader) &&
            x.ImplementationType == typeof(ServiceTrafficViewQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceDeploymentCatalogProjectionPort) &&
            x.ImplementationType == typeof(ServiceDeploymentCatalogProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceServingSetProjectionPort) &&
            x.ImplementationType == typeof(ServiceServingSetProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceRolloutProjectionPort) &&
            x.ImplementationType == typeof(ServiceRolloutProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceTrafficViewProjectionPort) &&
            x.ImplementationType == typeof(ServiceTrafficViewProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICurrentStateProjectionMaterializer<ServiceServingSetProjectionContext>) &&
            x.ImplementationType == typeof(ServiceServingSetProjector));
        services.Should().Contain(x =>
            x.ServiceType == typeof(ICurrentStateProjectionMaterializer<ServiceTrafficViewProjectionContext>) &&
            x.ImplementationType == typeof(ServiceTrafficViewProjector));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<ServiceDeploymentCatalogProjectionContext>) &&
            x.ImplementationType == typeof(ServiceDeploymentCatalogProjector));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<ServiceRolloutProjectionContext>) &&
            x.ImplementationType == typeof(ServiceRolloutProjector));
    }

    [Fact]
    public void DedicatedServiceProjectionEndpoints_ShouldValidateConstructorArguments()
    {
        var options = new ServiceProjectionOptions();
        var releaseCatalog = new NoOpProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>>();
        var releaseDeployment = new NoOpProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceDeploymentCatalogProjectionContext>>();
        var releaseRevision = new NoOpProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>>();
        var releaseServing = new NoOpProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceServingSetProjectionContext>>();
        var releaseRollout = new NoOpProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceRolloutProjectionContext>>();
        var releaseTraffic = new NoOpProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceTrafficViewProjectionContext>>();

        Action nullCatalog = () => new ServiceCatalogProjectionPort(
            null!,
            new RecordingProjectionActivationService<ServiceCatalogProjectionContext>((root, projectionName) => new ServiceCatalogProjectionContext { RootActorId = root, ProjectionKind = projectionName }),
            releaseCatalog);
        Action nullDeployment = () => new ServiceDeploymentCatalogProjectionPort(
            options,
            null!,
            releaseDeployment);
        Action nullRevision = () => new ServiceRevisionCatalogProjectionPort(
            options,
            null!,
            releaseRevision);
        Action nullServing = () => new ServiceServingSetProjectionPort(
            options,
            null!,
            releaseServing);
        Action nullRollout = () => new ServiceRolloutProjectionPort(
            options,
            null!,
            releaseRollout);
        Action nullTraffic = () => new ServiceTrafficViewProjectionPort(
            options,
            null!,
            releaseTraffic);

        nullCatalog.Should().Throw<ArgumentNullException>();
        nullDeployment.Should().Throw<ArgumentNullException>();
        nullRevision.Should().Throw<ArgumentNullException>();
        nullServing.Should().Throw<ArgumentNullException>();
        nullRollout.Should().Throw<ArgumentNullException>();
        nullTraffic.Should().Throw<ArgumentNullException>();
    }

}
