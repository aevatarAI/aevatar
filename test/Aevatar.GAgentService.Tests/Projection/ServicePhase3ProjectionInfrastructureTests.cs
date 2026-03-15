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

public sealed class ServicePhase3ProjectionInfrastructureTests
{
    [Fact]
    public async Task Phase3ProjectionPortServices_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var deploymentActivation = new RecordingActivationService<ServiceDeploymentCatalogRuntimeLease>(
            (root, projectionName) => new ServiceDeploymentCatalogRuntimeLease(new ServiceDeploymentCatalogProjectionContext
            {
                ProjectionId = $"{projectionName}:{root}",
                RootActorId = root,
            }));
        var servingActivation = new RecordingActivationService<ServiceServingSetRuntimeLease>(
            (root, projectionName) => new ServiceServingSetRuntimeLease(new ServiceServingSetProjectionContext
            {
                ProjectionId = $"{projectionName}:{root}",
                RootActorId = root,
            }));
        var rolloutActivation = new RecordingActivationService<ServiceRolloutRuntimeLease>(
            (root, projectionName) => new ServiceRolloutRuntimeLease(new ServiceRolloutProjectionContext
            {
                ProjectionId = $"{projectionName}:{root}",
                RootActorId = root,
            }));
        var trafficActivation = new RecordingActivationService<ServiceTrafficViewRuntimeLease>(
            (root, projectionName) => new ServiceTrafficViewRuntimeLease(new ServiceTrafficViewProjectionContext
            {
                ProjectionId = $"{projectionName}:{root}",
                RootActorId = root,
            }));

        await new ServiceDeploymentCatalogProjectionPortService(deploymentActivation).EnsureProjectionAsync("");
        await new ServiceDeploymentCatalogProjectionPortService(deploymentActivation).EnsureProjectionAsync("actor-deploy");
        await new ServiceServingSetProjectionPortService(servingActivation).EnsureProjectionAsync(" ");
        await new ServiceServingSetProjectionPortService(servingActivation).EnsureProjectionAsync("actor-serving");
        await new ServiceRolloutProjectionPortService(rolloutActivation).EnsureProjectionAsync("actor-rollout");
        await new ServiceTrafficViewProjectionPortService(trafficActivation).EnsureProjectionAsync("actor-traffic");

        deploymentActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-deploy", "service-deployments", string.Empty, "actor-deploy"));
        servingActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-serving", "service-serving", string.Empty, "actor-serving"));
        rolloutActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-rollout", "service-rollouts", string.Empty, "actor-rollout"));
        trafficActivation.Calls.Should().ContainSingle().Which.Should().Be(("actor-traffic", "service-traffic", string.Empty, "actor-traffic"));
    }

    [Fact]
    public async Task Phase3ActivationAndReleaseServices_ShouldCreateContext_AndStopWhenIdle()
    {
        var deploymentLifecycle = new RecordingLifecycle<ServiceDeploymentCatalogProjectionContext>();
        var deploymentLease = await new ServiceDeploymentCatalogProjectionActivationService(deploymentLifecycle)
            .EnsureAsync("actor-deploy", "service-deployments", string.Empty, "cmd-deploy");
        await new ServiceDeploymentCatalogProjectionReleaseService(deploymentLifecycle).ReleaseIfIdleAsync(deploymentLease);

        var servingLifecycle = new RecordingLifecycle<ServiceServingSetProjectionContext>();
        var servingLease = await new ServiceServingSetProjectionActivationService(servingLifecycle)
            .EnsureAsync("actor-serving", "service-serving", string.Empty, "cmd-serving");
        await new ServiceServingSetProjectionReleaseService(servingLifecycle).ReleaseIfIdleAsync(servingLease);

        var rolloutLifecycle = new RecordingLifecycle<ServiceRolloutProjectionContext>();
        var rolloutLease = await new ServiceRolloutProjectionActivationService(rolloutLifecycle)
            .EnsureAsync("actor-rollout", "service-rollouts", string.Empty, "cmd-rollout");
        await new ServiceRolloutProjectionReleaseService(rolloutLifecycle).ReleaseIfIdleAsync(rolloutLease);

        var trafficLifecycle = new RecordingLifecycle<ServiceTrafficViewProjectionContext>();
        var trafficLease = await new ServiceTrafficViewProjectionActivationService(trafficLifecycle)
            .EnsureAsync("actor-traffic", "service-traffic", string.Empty, "cmd-traffic");
        await new ServiceTrafficViewProjectionReleaseService(trafficLifecycle).ReleaseIfIdleAsync(trafficLease);

        deploymentLease.ScopeId.Should().Be("actor-deploy");
        deploymentLifecycle.StartedContexts.Single().ProjectionId.Should().Be("service-deployments:actor-deploy");
        deploymentLifecycle.StoppedContexts.Single().RootActorId.Should().Be("actor-deploy");
        servingLease.SessionId.Should().Be("actor-serving");
        servingLifecycle.StartedContexts.Single().ProjectionId.Should().Be("service-serving:actor-serving");
        rolloutLifecycle.StoppedContexts.Single().RootActorId.Should().Be("actor-rollout");
        trafficLifecycle.StartedContexts.Single().ProjectionId.Should().Be("service-traffic:actor-traffic");
    }

    [Fact]
    public void Phase3MetadataProviders_ShouldExposeStableIndexNames()
    {
        new ServiceDeploymentCatalogReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-deployments");
        new ServiceServingSetReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-serving");
        new ServiceRolloutReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-rollouts");
        new ServiceTrafficViewReadModelMetadataProvider().Metadata.IndexName.Should().Be("gagent-service-traffic");
    }

    [Fact]
    public void AddGAgentServiceProjection_ShouldRegisterPhase3ProjectionServices()
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

    private sealed class RecordingActivationService<TLease> : IProjectionPortActivationService<TLease>
        where TLease : IProjectionPortSessionLease
    {
        private readonly Func<string, string, TLease> _leaseFactory;

        public RecordingActivationService(Func<string, string, TLease> leaseFactory)
        {
            _leaseFactory = leaseFactory;
        }

        public List<(string rootEntityId, string projectionName, string input, string commandId)> Calls { get; } = [];

        public Task<TLease> EnsureAsync(
            string rootEntityId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            Calls.Add((rootEntityId, projectionName, input, commandId));
            return Task.FromResult(_leaseFactory(rootEntityId, projectionName));
        }
    }

    private sealed class RecordingLifecycle<TContext> : IProjectionLifecycleService<TContext, IReadOnlyList<string>>
        where TContext : class, IProjectionContext
    {
        public List<TContext> StartedContexts { get; } = [];

        public List<TContext> StoppedContexts { get; } = [];

        public Task StartAsync(TContext context, CancellationToken ct = default)
        {
            StartedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(TContext context, CancellationToken ct = default)
        {
            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(TContext context, IReadOnlyList<string> completion, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
