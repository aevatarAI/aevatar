using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
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
    public async Task GAgentRunTerminalProjectionPort_ShouldAttachExistingProjection_WhenScopeActorExists()
    {
        var runtime = new RecordingActorRuntime();
        runtime.KnownActorIds.Add(ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            "actor-1",
            "gagent-run-terminal-draft-run",
            ProjectionRuntimeMode.DurableMaterialization,
            "corr-1")));
        IGAgentRunTerminalProjectionPort service = new GAgentRunTerminalProjectionPort(
            new ServiceProjectionOptions(),
            new RecordingProjectionReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>(),
            CreateAttachExistingLookup<GAgentRunTerminalProjectionContext>(
                runtime,
                static scopeKey => new GAgentRunTerminalProjectionContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                    CorrelationId = scopeKey.SessionId,
                    InteractionKind = GAgentRunTerminalProjectionPort.ResolveInteractionKind(scopeKey.ProjectionKind),
                }));

        var lease = await service.AttachExistingProjectionAsync(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun);

        lease.Should().NotBeNull();
        lease!.ActorId.Should().Be("actor-1");
        lease.CorrelationId.Should().Be("corr-1");
        lease.InteractionKind.Should().Be(GAgentRunTerminalInteractionKind.DraftRun);
    }

    [Fact]
    public async Task GAgentRunTerminalProjectionPort_ShouldReturnNullForAttachExisting_WhenScopeActorIsMissingOrInvalid()
    {
        var runtime = new RecordingActorRuntime();
        runtime.KnownActorIds.Add("different-scope");
        IGAgentRunTerminalProjectionPort disabledService = new GAgentRunTerminalProjectionPort(
            new ServiceProjectionOptions { Enabled = false },
            new RecordingProjectionReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>(),
            CreateAttachExistingLookup<GAgentRunTerminalProjectionContext>(
                runtime,
                static scopeKey => new GAgentRunTerminalProjectionContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                    CorrelationId = scopeKey.SessionId,
                    InteractionKind = GAgentRunTerminalProjectionPort.ResolveInteractionKind(scopeKey.ProjectionKind),
                }));
        IGAgentRunTerminalProjectionPort enabledService = new GAgentRunTerminalProjectionPort(
            new ServiceProjectionOptions(),
            new RecordingProjectionReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>(),
            CreateAttachExistingLookup<GAgentRunTerminalProjectionContext>(
                runtime,
                static scopeKey => new GAgentRunTerminalProjectionContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                    CorrelationId = scopeKey.SessionId,
                    InteractionKind = GAgentRunTerminalProjectionPort.ResolveInteractionKind(scopeKey.ProjectionKind),
                }));

        (await disabledService.AttachExistingProjectionAsync(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun)).Should().BeNull();
        (await enabledService.AttachExistingProjectionAsync(
            "actor-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun)).Should().BeNull();
        (await enabledService.AttachExistingProjectionAsync(
            "",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun)).Should().BeNull();
        (await enabledService.AttachExistingProjectionAsync(
            "actor-1",
            " ",
            GAgentRunTerminalInteractionKind.DraftRun)).Should().BeNull();
    }

    [Fact]
    public async Task GAgentRunTerminalProjectionPort_ShouldGuardReleaseAndUnknownKinds()
    {
        IGAgentRunTerminalProjectionPort service = new GAgentRunTerminalProjectionPort(
            new ServiceProjectionOptions(),
            new RecordingProjectionReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>(),
            CreateAttachExistingLookup<GAgentRunTerminalProjectionContext>(
                new RecordingActorRuntime(),
                static scopeKey => new GAgentRunTerminalProjectionContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                    CorrelationId = scopeKey.SessionId,
                    InteractionKind = GAgentRunTerminalProjectionPort.ResolveInteractionKind(scopeKey.ProjectionKind),
                }));

        Func<Task> releaseNull = () => service.ReleaseProjectionAsync(null!);
        Func<Task> releaseForeignLease = () => service.ReleaseProjectionAsync(new ForeignGAgentRunTerminalProjectionLease());
        Func<Task> ensureUnknownKind = () => service.AttachExistingProjectionAsync(
            "actor-1",
            "corr-1",
            (GAgentRunTerminalInteractionKind)999);
        var resolveUnknownProjection = () => GAgentRunTerminalProjectionPort.ResolveInteractionKind("unknown-projection");

        await releaseNull.Should().ThrowAsync<ArgumentNullException>();
        await releaseForeignLease.Should().ThrowAsync<InvalidOperationException>();
        await ensureUnknownKind.Should().ThrowAsync<ArgumentOutOfRangeException>();
        resolveUnknownProjection.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GAgentRunTerminalProjectionPort_ShouldValidateAttachExistingLookupDependency()
    {
        var create = () => new GAgentRunTerminalProjectionPort(
            new ServiceProjectionOptions(),
            new RecordingProjectionReleaseService<ServiceProjectionRuntimeLease<GAgentRunTerminalProjectionContext>>(),
            null!);

        create.Should().Throw<ArgumentNullException>().WithParameterName("attachExistingLeaseLookup");
    }

    [Fact]
    public void GAgentRunTerminalModels_ShouldExposeStableSessionContextAndSnapshotShape()
    {
        var context = new GAgentRunTerminalProjectionContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "gagent-run-terminal-draft-run",
            CorrelationId = "corr-1",
            InteractionKind = GAgentRunTerminalInteractionKind.DraftRun,
        };
        var observedAt = DateTimeOffset.Parse("2026-05-14T01:00:00+00:00");
        var snapshot = new GAgentRunTerminalSnapshot(
            "actor-1",
            "session-1",
            "corr-1",
            GAgentRunTerminalInteractionKind.DraftRun,
            GAgentRunTerminalStatus.TextMessageCompleted,
            "",
            "",
            14,
            "evt-14",
            observedAt);

        context.SessionId.Should().Be("corr-1");
        var copy = snapshot with { };
        copy.Should().Be(snapshot);
        var (
            actorId,
            sessionId,
            correlationId,
            interactionKind,
            status,
            reasonCode,
            reasonMessage,
            stateVersion,
            lastEventId,
            actualObservedAt) = snapshot;
        actorId.Should().Be("actor-1");
        sessionId.Should().Be("session-1");
        correlationId.Should().Be("corr-1");
        interactionKind.Should().Be(GAgentRunTerminalInteractionKind.DraftRun);
        status.Should().Be(GAgentRunTerminalStatus.TextMessageCompleted);
        reasonCode.Should().BeEmpty();
        reasonMessage.Should().BeEmpty();
        stateVersion.Should().Be(14);
        lastEventId.Should().Be("evt-14");
        actualObservedAt.Should().Be(observedAt);
    }

    [Fact]
    public void MetadataProviders_ShouldExposeStableIndexNames()
    {
        var catalog = new ServiceCatalogReadModelMetadataProvider();
        var revisions = new ServiceRevisionCatalogReadModelMetadataProvider();
        var terminal = new GAgentRunTerminalReadModelMetadataProvider();

        catalog.Metadata.IndexName.Should().Be("gagent-service-catalog");
        revisions.Metadata.IndexName.Should().Be("gagent-service-revisions");
        terminal.Metadata.IndexName.Should().Be("gagent-run-terminals");

        var properties = catalog.Metadata.Mappings["properties"].Should()
            .BeAssignableTo<IReadOnlyDictionary<string, object?>>()
            .Subject;
        var namespaceMapping = properties["namespace"].Should()
            .BeAssignableTo<IReadOnlyDictionary<string, object?>>()
            .Subject;
        namespaceMapping["type"].Should().Be("keyword");

        revisions.Metadata.Settings.Should().BeEmpty();
        terminal.Metadata.Mappings.Should().BeEmpty();
        terminal.Metadata.Settings.Should().BeEmpty();
        terminal.Metadata.Aliases.Should().BeEmpty();
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
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<GAgentRunTerminalReadModel>) &&
            x.ImplementationType == typeof(GAgentRunTerminalReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceCatalogQueryReader) &&
            x.ImplementationType == typeof(ServiceCatalogQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceRevisionCatalogQueryReader) &&
            x.ImplementationType == typeof(ServiceRevisionCatalogQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IGAgentRunTerminalQueryPort) &&
            x.ImplementationType == typeof(GAgentRunTerminalQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IGAgentRunTerminalProjectionPort) &&
            x.ImplementationType == typeof(GAgentRunTerminalProjectionPort));
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<ServiceCatalogProjectionContext>) &&
            IsObservedProjectionArtifactMaterializerFor<ServiceCatalogProjector>(x.ImplementationType));
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<ServiceRevisionCatalogProjectionContext>) &&
            IsObservedProjectionArtifactMaterializerFor<ServiceRevisionCatalogProjector>(x.ImplementationType));
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(ICurrentStateProjectionMaterializer<GAgentRunTerminalProjectionContext>) &&
            IsObservedCurrentStateMaterializerFor<GAgentRunTerminalProjector>(x.ImplementationType));
    }

    private static bool IsObservedProjectionArtifactMaterializerFor<TProjector>(System.Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedProjectionArtifactMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TProjector);
    }

    private static bool IsObservedCurrentStateMaterializerFor<TProjector>(System.Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedCurrentStateProjectionMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TProjector);
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

    private sealed class ForeignGAgentRunTerminalProjectionLease : IGAgentRunTerminalProjectionLease
    {
        public string ActorId => "actor-foreign";

        public string CorrelationId => "corr-foreign";

        public GAgentRunTerminalInteractionKind InteractionKind => GAgentRunTerminalInteractionKind.DraftRun;
    }

    private static IProjectionScopeAttachExistingLeaseLookup<ServiceProjectionRuntimeLease<TContext>> CreateAttachExistingLookup<TContext>(
        IActorRuntime runtime,
        Func<ProjectionRuntimeScopeKey, TContext> contextFactory)
        where TContext : class, IProjectionMaterializationContext =>
        new ProjectionScopeAttachExistingLeaseLookup<ServiceProjectionRuntimeLease<TContext>, TContext>(
            runtime,
            request => contextFactory(new ProjectionRuntimeScopeKey(
                request.RootActorId,
                request.ProjectionKind,
                request.Mode,
                request.SessionId)),
            static (_, context) => new ServiceProjectionRuntimeLease<TContext>(context.RootActorId, context));
}
