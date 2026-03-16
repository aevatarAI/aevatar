using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceServingProjectionInfrastructureTests
{
    [Fact]
    public async Task ServingProjectionPortServices_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var deploymentActivation = new RecordingProjectionActivationService<ServiceDeploymentCatalogProjectionContext>(
            (root, projectionName) => new ServiceDeploymentCatalogProjectionContext
            {
                ProjectionId = $"{projectionName}:{root}",
                RootActorId = root,
            });
        var servingActivation = new RecordingProjectionActivationService<ServiceServingSetProjectionContext>(
            (root, projectionName) => new ServiceServingSetProjectionContext
            {
                ProjectionId = $"{projectionName}:{root}",
                RootActorId = root,
            });
        var rolloutActivation = new RecordingProjectionActivationService<ServiceRolloutProjectionContext>(
            (root, projectionName) => new ServiceRolloutProjectionContext
            {
                ProjectionId = $"{projectionName}:{root}",
                RootActorId = root,
            });
        var trafficActivation = new RecordingProjectionActivationService<ServiceTrafficViewProjectionContext>(
            (root, projectionName) => new ServiceTrafficViewProjectionContext
            {
                ProjectionId = $"{projectionName}:{root}",
                RootActorId = root,
            });

        var portServices = new ServiceProjectionPortServices(
            new RecordingProjectionActivationService<ServiceCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            deploymentActivation,
            new RecordingProjectionActivationService<ServiceRevisionCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            servingActivation,
            rolloutActivation,
            trafficActivation);

        await ((IServiceDeploymentCatalogProjectionPort)portServices).EnsureProjectionAsync("");
        await ((IServiceDeploymentCatalogProjectionPort)portServices).EnsureProjectionAsync("actor-deploy");
        await ((IServiceServingSetProjectionPort)portServices).EnsureProjectionAsync(" ");
        await ((IServiceServingSetProjectionPort)portServices).EnsureProjectionAsync("actor-serving");
        await ((IServiceRolloutProjectionPort)portServices).EnsureProjectionAsync("actor-rollout");
        await ((IServiceTrafficViewProjectionPort)portServices).EnsureProjectionAsync("actor-traffic");

        deploymentActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-deploy", "service-deployments", string.Empty, "actor-deploy"));
        servingActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-serving", "service-serving", string.Empty, "actor-serving"));
        rolloutActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-rollout", "service-rollouts", string.Empty, "actor-rollout"));
        trafficActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-traffic", "service-traffic", string.Empty, "actor-traffic"));
    }

    [Fact]
    public async Task ServingActivationAndReleaseServices_ShouldCreateContext_AndStopWhenIdle()
    {
        var deploymentLifecycle = new RecordingProjectionLifecycle<ServiceDeploymentCatalogProjectionContext>();
        var deploymentLease = await new ServiceProjectionActivationService<ServiceDeploymentCatalogProjectionContext>(
                new ServiceProjectionDescriptor<ServiceDeploymentCatalogProjectionContext>(
                    static (rootActorId, projectionName) => new ServiceDeploymentCatalogProjectionContext
                    {
                        ProjectionId = $"{projectionName}:{rootActorId}",
                        RootActorId = rootActorId,
                    },
                    static context => context.RootActorId),
                deploymentLifecycle)
            .EnsureAsync("actor-deploy", "service-deployments", string.Empty, "cmd-deploy");
        await new ServiceProjectionReleaseService<ServiceDeploymentCatalogProjectionContext>(deploymentLifecycle).ReleaseIfIdleAsync(deploymentLease);

        var servingLifecycle = new RecordingProjectionLifecycle<ServiceServingSetProjectionContext>();
        var servingLease = await new ServiceProjectionActivationService<ServiceServingSetProjectionContext>(
                new ServiceProjectionDescriptor<ServiceServingSetProjectionContext>(
                    static (rootActorId, projectionName) => new ServiceServingSetProjectionContext
                    {
                        ProjectionId = $"{projectionName}:{rootActorId}",
                        RootActorId = rootActorId,
                    },
                    static context => context.RootActorId),
                servingLifecycle)
            .EnsureAsync("actor-serving", "service-serving", string.Empty, "cmd-serving");
        await new ServiceProjectionReleaseService<ServiceServingSetProjectionContext>(servingLifecycle).ReleaseIfIdleAsync(servingLease);

        var rolloutLifecycle = new RecordingProjectionLifecycle<ServiceRolloutProjectionContext>();
        var rolloutLease = await new ServiceProjectionActivationService<ServiceRolloutProjectionContext>(
                new ServiceProjectionDescriptor<ServiceRolloutProjectionContext>(
                    static (rootActorId, projectionName) => new ServiceRolloutProjectionContext
                    {
                        ProjectionId = $"{projectionName}:{rootActorId}",
                        RootActorId = rootActorId,
                    },
                    static context => context.RootActorId),
                rolloutLifecycle)
            .EnsureAsync("actor-rollout", "service-rollouts", string.Empty, "cmd-rollout");
        await new ServiceProjectionReleaseService<ServiceRolloutProjectionContext>(rolloutLifecycle).ReleaseIfIdleAsync(rolloutLease);

        var trafficLifecycle = new RecordingProjectionLifecycle<ServiceTrafficViewProjectionContext>();
        var trafficLease = await new ServiceProjectionActivationService<ServiceTrafficViewProjectionContext>(
                new ServiceProjectionDescriptor<ServiceTrafficViewProjectionContext>(
                    static (rootActorId, projectionName) => new ServiceTrafficViewProjectionContext
                    {
                        ProjectionId = $"{projectionName}:{rootActorId}",
                        RootActorId = rootActorId,
                    },
                    static context => context.RootActorId),
                trafficLifecycle)
            .EnsureAsync("actor-traffic", "service-traffic", string.Empty, "cmd-traffic");
        await new ServiceProjectionReleaseService<ServiceTrafficViewProjectionContext>(trafficLifecycle).ReleaseIfIdleAsync(trafficLease);

        deploymentLease.ScopeId.Should().Be("actor-deploy");
        deploymentLifecycle.StartedContexts.Single().ProjectionId.Should().Be("service-deployments:actor-deploy");
        deploymentLifecycle.StoppedContexts.Single().RootActorId.Should().Be("actor-deploy");
        servingLease.SessionId.Should().Be("actor-serving");
        servingLifecycle.StartedContexts.Single().ProjectionId.Should().Be("service-serving:actor-serving");
        rolloutLifecycle.StoppedContexts.Single().RootActorId.Should().Be("actor-rollout");
        trafficLifecycle.StartedContexts.Single().ProjectionId.Should().Be("service-traffic:actor-traffic");
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
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceServingSetProjectionPort) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceRolloutProjectionPort) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceTrafficViewProjectionPort) &&
            x.ImplementationFactory != null);
    }

    [Fact]
    public void ServiceProjectionPortServices_ShouldValidateConstructorArguments()
    {
        Action nullCatalog = () => new ServiceProjectionPortServices(
            null!,
            new RecordingProjectionActivationService<ServiceDeploymentCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRevisionCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceServingSetProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRolloutProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceTrafficViewProjectionContext>((_, _) => throw new NotSupportedException()));
        Action nullDeployment = () => new ServiceProjectionPortServices(
            new RecordingProjectionActivationService<ServiceCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            null!,
            new RecordingProjectionActivationService<ServiceRevisionCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceServingSetProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRolloutProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceTrafficViewProjectionContext>((_, _) => throw new NotSupportedException()));
        Action nullRevision = () => new ServiceProjectionPortServices(
            new RecordingProjectionActivationService<ServiceCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceDeploymentCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            null!,
            new RecordingProjectionActivationService<ServiceServingSetProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRolloutProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceTrafficViewProjectionContext>((_, _) => throw new NotSupportedException()));
        Action nullServing = () => new ServiceProjectionPortServices(
            new RecordingProjectionActivationService<ServiceCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceDeploymentCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRevisionCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            null!,
            new RecordingProjectionActivationService<ServiceRolloutProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceTrafficViewProjectionContext>((_, _) => throw new NotSupportedException()));
        Action nullRollout = () => new ServiceProjectionPortServices(
            new RecordingProjectionActivationService<ServiceCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceDeploymentCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRevisionCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceServingSetProjectionContext>((_, _) => throw new NotSupportedException()),
            null!,
            new RecordingProjectionActivationService<ServiceTrafficViewProjectionContext>((_, _) => throw new NotSupportedException()));
        Action nullTraffic = () => new ServiceProjectionPortServices(
            new RecordingProjectionActivationService<ServiceCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceDeploymentCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRevisionCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceServingSetProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRolloutProjectionContext>((_, _) => throw new NotSupportedException()),
            null!);

        nullCatalog.Should().Throw<ArgumentNullException>();
        nullDeployment.Should().Throw<ArgumentNullException>();
        nullRevision.Should().Throw<ArgumentNullException>();
        nullServing.Should().Throw<ArgumentNullException>();
        nullRollout.Should().Throw<ArgumentNullException>();
        nullTraffic.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ServiceProjectionActivationService_ShouldValidateDescriptor()
    {
        Action act = () => new ServiceProjectionActivationService<ServiceDeploymentCatalogProjectionContext>(
            null!,
            new RecordingProjectionLifecycle<ServiceDeploymentCatalogProjectionContext>());

        act.Should().Throw<ArgumentNullException>();
    }
}
