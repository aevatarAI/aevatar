using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Configuration;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceProjectionInfrastructureTests
{
    [Fact]
    public async Task CatalogProjectionPort_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var activationService = new RecordingProjectionActivationService<ServiceCatalogProjectionContext>(
            static (rootActorId, projectionName) => new ServiceCatalogProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            });
        IServiceCatalogProjectionPort service = new ServiceCatalogProjectionPort(
            new ServiceProjectionOptions(),
            activationService,
            new RecordingProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>>());

        await service.EnsureProjectionAsync(string.Empty);
        await service.EnsureProjectionAsync("actor-1");

        activationService.Calls.Should().ContainSingle();
        activationService.Calls[0].Should().Be(("actor-1", "service-catalog"));
    }

    [Fact]
    public async Task RevisionProjectionPort_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var activationService = new RecordingProjectionActivationService<ServiceRevisionCatalogProjectionContext>(
            static (rootActorId, projectionName) => new ServiceRevisionCatalogProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            });
        IServiceRevisionCatalogProjectionPort service = new ServiceRevisionCatalogProjectionPort(
            new ServiceProjectionOptions(),
            activationService,
            new RecordingProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>>());

        await service.EnsureProjectionAsync(" ");
        await service.EnsureProjectionAsync("actor-2");

        activationService.Calls.Should().ContainSingle();
        activationService.Calls[0].Should().Be(("actor-2", "service-revisions"));
    }

    [Fact]
    public async Task ProjectionPorts_ShouldSkipActivation_WhenDisabled()
    {
        var catalogActivation = new RecordingProjectionActivationService<ServiceCatalogProjectionContext>(
            static (rootActorId, projectionName) => new ServiceCatalogProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            });
        var revisionActivation = new RecordingProjectionActivationService<ServiceRevisionCatalogProjectionContext>(
            static (rootActorId, projectionName) => new ServiceRevisionCatalogProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            });
        var disabledOptions = new ServiceProjectionOptions { Enabled = false };
        IServiceCatalogProjectionPort catalogPort = new ServiceCatalogProjectionPort(
            disabledOptions,
            catalogActivation,
            new RecordingProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>>());
        IServiceRevisionCatalogProjectionPort revisionPort = new ServiceRevisionCatalogProjectionPort(
            disabledOptions,
            revisionActivation,
            new RecordingProjectionReleaseService<ServiceProjectionRuntimeLease<ServiceRevisionCatalogProjectionContext>>());

        await catalogPort.EnsureProjectionAsync("actor-1");
        await revisionPort.EnsureProjectionAsync("actor-2");

        catalogActivation.Calls.Should().BeEmpty();
        revisionActivation.Calls.Should().BeEmpty();
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
            x.ImplementationType == typeof(ServiceCatalogProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceRevisionCatalogProjectionPort) &&
            x.ImplementationType == typeof(ServiceRevisionCatalogProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<ServiceCatalogProjectionContext>) &&
            x.ImplementationType == typeof(ServiceCatalogProjector));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<ServiceRevisionCatalogProjectionContext>) &&
            x.ImplementationType == typeof(ServiceRevisionCatalogProjector));
    }

    [Fact]
    public void ProjectionHelpers_ShouldGuardConstructorInputs_AndMapFallbackValues()
    {
        var runtimeLease = () => new ServiceProjectionRuntimeLease<ServiceCatalogProjectionContext>("actor-1", null!);

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

    [Fact]
    public void ProjectionHelpers_ShouldMapSnapshots_AndResolveCommittedStateSupportBranches()
    {
        var assembly = typeof(ServiceCatalogReadModelMetadataProvider).Assembly;
        var mappingType = assembly.GetType("Aevatar.GAgentService.Projection.Internal.ServiceProjectionMapping", throwOnError: true)!;
        var supportType = assembly.GetType("Aevatar.GAgentService.Projection.Internal.ServiceCommittedStateSupport", throwOnError: true)!;
        var targetSnapshot = (ServiceServingTargetSnapshot)mappingType
            .GetMethod("ToServingTargetSnapshot", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [new ServiceServingTargetReadModel
            {
                DeploymentId = "dep-1",
                RevisionId = "rev-1",
                PrimaryActorId = "actor-1",
                AllocationWeight = 80,
                ServingState = ServiceServingState.Active.ToString(),
                EnabledEndpointIds = { "run", "chat" },
            }])!;
        var trafficSnapshot = (ServiceTrafficTargetSnapshot)mappingType
            .GetMethod("ToTrafficTargetSnapshot", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [new ServiceTrafficTargetReadModel
            {
                DeploymentId = "dep-1",
                RevisionId = "rev-1",
                PrimaryActorId = "actor-1",
                AllocationWeight = 20,
                ServingState = ServiceServingState.Paused.ToString(),
            }])!;
        var committedArgs = new object?[]
        {
            new EventEnvelope
            {
                Id = "outer-1",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-16T01:05:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-1",
                        Version = 5,
                        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-16T01:00:00+00:00")),
                        EventData = Any.Pack(new StringValue { Value = "payload" }),
                    },
                }),
            },
            new FixedProjectionClock(DateTimeOffset.Parse("2026-03-16T02:00:00+00:00")),
            null,
            null,
            null,
            null,
        };
        var committedResult = (bool)supportType
            .GetMethod("TryGetObservedPayload", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, committedArgs)!;
        var invalidCommittedArgs = new object?[]
        {
            new EventEnvelope
            {
                Id = "outer-2",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-16T03:05:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-2",
                        Version = 0,
                    },
                }),
            },
            new FixedProjectionClock(DateTimeOffset.Parse("2026-03-16T03:00:00+00:00")),
            null,
            null,
            null,
            null,
        };
        var invalidCommittedResult = (bool)supportType
            .GetMethod("TryGetObservedPayload", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, invalidCommittedArgs)!;
        var plainArgs = new object?[]
        {
            new EventEnvelope
            {
                Id = "plain-1",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-16T04:00:00+00:00")),
                Payload = Any.Pack(new StringValue { Value = "plain" }),
            },
            new FixedProjectionClock(DateTimeOffset.Parse("2026-03-16T05:00:00+00:00")),
            null,
            null,
            null,
            null,
        };
        var plainResult = (bool)supportType
            .GetMethod("TryGetObservedPayload", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, plainArgs)!;
        var resolvedVersion = (long)supportType
            .GetMethod("ResolveNextStateVersion", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [3L, 0L])!;

        targetSnapshot.EnabledEndpointIds.Should().Equal("run", "chat");
        targetSnapshot.ServingState.Should().Be(ServiceServingState.Active.ToString());
        trafficSnapshot.ServingState.Should().Be(ServiceServingState.Paused.ToString());
        committedResult.Should().BeTrue();
        ((Any)committedArgs[2]!).Is(StringValue.Descriptor).Should().BeTrue();
        committedArgs[3].Should().Be("evt-1");
        committedArgs[4].Should().Be(5L);
        committedArgs[5].Should().Be(DateTimeOffset.Parse("2026-03-16T01:00:00+00:00"));
        invalidCommittedResult.Should().BeFalse();
        invalidCommittedArgs[2].Should().BeNull();
        invalidCommittedArgs[3].Should().Be(string.Empty);
        invalidCommittedArgs[4].Should().Be(0L);
        invalidCommittedArgs[5].Should().Be(default(DateTimeOffset));
        plainResult.Should().BeFalse();
        plainArgs[2].Should().BeNull();
        plainArgs[3].Should().Be(string.Empty);
        plainArgs[4].Should().Be(0L);
        plainArgs[5].Should().Be(default(DateTimeOffset));
        resolvedVersion.Should().Be(0L);
    }

    [Fact]
    public void ProjectionHelpers_ShouldMapNonFallbackServiceKeyTimestampAndTargets()
    {
        var assembly = typeof(ServiceCatalogReadModelMetadataProvider).Assembly;
        var mappingType = assembly.GetType("Aevatar.GAgentService.Projection.Internal.ServiceProjectionMapping", throwOnError: true)!;
        var serviceKey = (string)mappingType
            .GetMethod("ServiceKey", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [new ServiceIdentity
            {
                TenantId = "tenant",
                AppId = "app",
                Namespace = "default",
                ServiceId = "svc",
            }])!;
        var timestamp = (DateTimeOffset)mappingType
            .GetMethod("FromTimestamp", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-16T06:00:00+00:00")), DateTimeOffset.UnixEpoch])!;
        var target = (ServiceServingTargetReadModel)mappingType
            .GetMethod("ToServingTargetReadModel", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [new ServiceServingTargetSpec
            {
                DeploymentId = "dep-1",
                RevisionId = "rev-1",
                PrimaryActorId = "actor-1",
                AllocationWeight = 90,
                ServingState = ServiceServingState.Draining,
                EnabledEndpointIds = { "run", "chat" },
            }])!;
        var traffic = (ServiceTrafficTargetReadModel)mappingType
            .GetMethod("ToTrafficTargetReadModel", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [new ServiceServingTargetSpec
            {
                DeploymentId = "dep-1",
                RevisionId = "rev-1",
                PrimaryActorId = "actor-1",
                AllocationWeight = 10,
                ServingState = ServiceServingState.Paused,
            }])!;

        serviceKey.Should().Be("tenant:app:default:svc");
        timestamp.Should().Be(DateTimeOffset.Parse("2026-03-16T06:00:00+00:00"));
        target.DeploymentId.Should().Be("dep-1");
        target.RevisionId.Should().Be("rev-1");
        target.PrimaryActorId.Should().Be("actor-1");
        target.AllocationWeight.Should().Be(90);
        target.ServingState.Should().Be(ServiceServingState.Draining.ToString());
        target.EnabledEndpointIds.Should().Equal("run", "chat");
        traffic.DeploymentId.Should().Be("dep-1");
        traffic.RevisionId.Should().Be("rev-1");
        traffic.PrimaryActorId.Should().Be("actor-1");
        traffic.AllocationWeight.Should().Be(10);
        traffic.ServingState.Should().Be(ServiceServingState.Paused.ToString());
    }
}
