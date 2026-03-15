using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceProjectionInfrastructureTests
{
    [Fact]
    public async Task CatalogProjectionPortService_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var activationService = new RecordingProjectionActivationService<ServiceCatalogProjectionContext>(
            static (rootActorId, projectionName) => new ServiceCatalogProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootActorId}",
                RootActorId = rootActorId,
            });
        IServiceCatalogProjectionPort service = new ServiceProjectionPortServices(
            activationService,
            new RecordingProjectionActivationService<ServiceDeploymentCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRevisionCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceServingSetProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRolloutProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceTrafficViewProjectionContext>((_, _) => throw new NotSupportedException()));

        await service.EnsureProjectionAsync(string.Empty);
        await service.EnsureProjectionAsync("actor-1");

        activationService.Calls.Should().ContainSingle();
        activationService.Calls[0].Should().Be(("actor-1", "service-catalog", string.Empty, "actor-1"));
    }

    [Fact]
    public async Task RevisionProjectionPortService_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var activationService = new RecordingProjectionActivationService<ServiceRevisionCatalogProjectionContext>(
            static (rootActorId, projectionName) => new ServiceRevisionCatalogProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootActorId}",
                RootActorId = rootActorId,
            });
        IServiceRevisionCatalogProjectionPort service = new ServiceProjectionPortServices(
            new RecordingProjectionActivationService<ServiceCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceDeploymentCatalogProjectionContext>((_, _) => throw new NotSupportedException()),
            activationService,
            new RecordingProjectionActivationService<ServiceServingSetProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceRolloutProjectionContext>((_, _) => throw new NotSupportedException()),
            new RecordingProjectionActivationService<ServiceTrafficViewProjectionContext>((_, _) => throw new NotSupportedException()));

        await service.EnsureProjectionAsync(" ");
        await service.EnsureProjectionAsync("actor-2");

        activationService.Calls.Should().ContainSingle();
        activationService.Calls[0].Should().Be(("actor-2", "service-revisions", string.Empty, "actor-2"));
    }

    [Fact]
    public async Task ActivationAndReleaseServices_ShouldCreateContext_AndStopWhenIdle()
    {
        var catalogLifecycle = new RecordingProjectionLifecycle<ServiceCatalogProjectionContext>();
        var activation = new ServiceProjectionActivationService<ServiceCatalogProjectionContext>(
            new ServiceProjectionDescriptor<ServiceCatalogProjectionContext>(
                static (rootActorId, projectionName) => new ServiceCatalogProjectionContext
                {
                    ProjectionId = $"{projectionName}:{rootActorId}",
                    RootActorId = rootActorId,
                },
                static context => context.RootActorId),
            catalogLifecycle);
        var release = new ServiceProjectionReleaseService<ServiceCatalogProjectionContext>(catalogLifecycle);

        var lease = await activation.EnsureAsync("actor-1", "service-catalog", string.Empty, "cmd-1");
        await release.ReleaseIfIdleAsync(lease);

        lease.ScopeId.Should().Be("actor-1");
        lease.SessionId.Should().Be("actor-1");
        catalogLifecycle.StartedContexts.Should().ContainSingle();
        catalogLifecycle.StartedContexts[0].ProjectionId.Should().Be("service-catalog:actor-1");
        catalogLifecycle.StoppedContexts.Should().ContainSingle();
        catalogLifecycle.StoppedContexts[0].RootActorId.Should().Be("actor-1");

        var revisionLifecycle = new RecordingProjectionLifecycle<ServiceRevisionCatalogProjectionContext>();
        var revisionActivation = new ServiceProjectionActivationService<ServiceRevisionCatalogProjectionContext>(
            new ServiceProjectionDescriptor<ServiceRevisionCatalogProjectionContext>(
                static (rootActorId, projectionName) => new ServiceRevisionCatalogProjectionContext
                {
                    ProjectionId = $"{projectionName}:{rootActorId}",
                    RootActorId = rootActorId,
                },
                static context => context.RootActorId),
            revisionLifecycle);
        var revisionRelease = new ServiceProjectionReleaseService<ServiceRevisionCatalogProjectionContext>(revisionLifecycle);
        var revisionLease = await revisionActivation.EnsureAsync("actor-2", "service-revisions", string.Empty, "cmd-2");
        await revisionRelease.ReleaseIfIdleAsync(revisionLease);

        revisionLifecycle.StartedContexts.Should().ContainSingle();
        revisionLifecycle.StartedContexts[0].ProjectionId.Should().Be("service-revisions:actor-2");
        revisionLifecycle.StoppedContexts.Should().ContainSingle();
        revisionLease.ScopeId.Should().Be("actor-2");
    }

    [Fact]
    public void MetadataProviders_ShouldExposeStableIndexNames()
    {
        var catalog = new ServiceCatalogReadModelMetadataProvider();
        var revisions = new ServiceRevisionCatalogReadModelMetadataProvider();

        catalog.Metadata.IndexName.Should().Be("gagent-service-catalog");
        revisions.Metadata.IndexName.Should().Be("gagent-service-revisions");
        catalog.Metadata.Mappings.Should().BeEmpty();
        revisions.Metadata.Settings.Should().BeEmpty();
    }

    [Fact]
    public void AddGAgentServiceProjection_ShouldRegisterProjectionServices()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceCatalogReadModel>) &&
            x.ImplementationType == typeof(ServiceCatalogReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceRevisionCatalogReadModel>) &&
            x.ImplementationType == typeof(ServiceRevisionCatalogReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceCatalogQueryReader) &&
            x.ImplementationType == typeof(ServiceCatalogQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceRevisionCatalogQueryReader) &&
            x.ImplementationType == typeof(ServiceRevisionCatalogQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceCatalogProjectionPort) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceRevisionCatalogProjectionPort) &&
            x.ImplementationFactory != null);
    }

    [Fact]
    public void ProjectionHelpers_ShouldGuardConstructorInputs_AndMapFallbackValues()
    {
        var descriptorFactory = () => new ServiceProjectionDescriptor<ServiceCatalogProjectionContext>(
            null!,
            static _ => "actor-1");
        var descriptorSelector = () => new ServiceProjectionDescriptor<ServiceCatalogProjectionContext>(
            static (_, _) => new ServiceCatalogProjectionContext
            {
                ProjectionId = "projection-1",
                RootActorId = "actor-1",
            },
            null!);
        var runtimeLease = () => new ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>("actor-1", null!);

        descriptorFactory.Should().Throw<ArgumentNullException>();
        descriptorSelector.Should().Throw<ArgumentNullException>();
        runtimeLease.Should().Throw<ArgumentNullException>();

        var mappingType = typeof(ServiceCatalogReadModelMetadataProvider).Assembly
            .GetType("Aevatar.GAgentService.Projection.Internal.ServiceProjectionMapping", throwOnError: true)!;
        var serviceKey = (string)mappingType
            .GetMethod("ServiceKey", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [null])!;
        var fallback = (DateTimeOffset)mappingType
            .GetMethod("FromTimestamp", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [null, DateTimeOffset.UnixEpoch])!;
        var target = (ServiceServingTargetReadModel)mappingType
            .GetMethod("ToServingTargetReadModel", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [new ServiceServingTargetSpec()])!;
        var traffic = (ServiceTrafficTargetReadModel)mappingType
            .GetMethod("ToTrafficTargetReadModel", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [new ServiceServingTargetSpec()])!;

        serviceKey.Should().BeEmpty();
        fallback.Should().Be(DateTimeOffset.UnixEpoch);
        target.DeploymentId.Should().BeEmpty();
        target.RevisionId.Should().BeEmpty();
        target.PrimaryActorId.Should().BeEmpty();
        target.AllocationWeight.Should().Be(0);
        target.EnabledEndpointIds.Should().BeEmpty();
        traffic.DeploymentId.Should().BeEmpty();
        traffic.ServingState.Should().Be(ServiceServingState.Unspecified.ToString());
    }
}
