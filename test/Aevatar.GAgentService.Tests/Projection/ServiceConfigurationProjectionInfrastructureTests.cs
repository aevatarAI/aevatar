using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.DependencyInjection;
using Aevatar.GAgentService.Governance.Projection.Metadata;
using Aevatar.GAgentService.Governance.Projection.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Projectors;
using Aevatar.GAgentService.Governance.Projection.Queries;
using Aevatar.GAgentService.Governance.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceConfigurationProjectionInfrastructureTests
{
    [Fact]
    public async Task ConfigurationProjectionPortService_ShouldIgnoreBlankActorId_AndEnsureLease()
    {
        var activationService = new RecordingConfigurationActivationService();
        var service = new ServiceConfigurationProjectionPortService(activationService);

        await service.EnsureProjectionAsync(string.Empty);
        await service.EnsureProjectionAsync("config-actor");

        activationService.Calls.Should().ContainSingle();
        activationService.Calls[0].Should().Be(("config-actor", "service-configuration", string.Empty, "config-actor"));
    }

    [Fact]
    public async Task ActivationAndReleaseServices_ShouldCreateContextLeaseAndStopWhenIdle()
    {
        var lifecycle = new RecordingConfigurationLifecycle();
        var activation = new ServiceConfigurationProjectionActivationService(lifecycle);
        var release = new ServiceConfigurationProjectionReleaseService(lifecycle);
        var lease = await activation.EnsureAsync("config-actor", "service-configuration", string.Empty, "cmd-config");
        await release.ReleaseIfIdleAsync(lease);

        lease.ScopeId.Should().Be("config-actor");
        lease.SessionId.Should().Be("config-actor");
        lifecycle.StartedContexts.Should().ContainSingle();
        lifecycle.StartedContexts[0].ProjectionId.Should().Be("service-configuration:config-actor");
        lifecycle.StoppedContexts.Should().ContainSingle();
        ((IProjectionContext)lease.Context).ProjectionId.Should().Be("service-configuration:config-actor");
    }

    [Fact]
    public void MetadataProviders_ShouldExposeStableIndexNames()
    {
        var metadataProvider = new ServiceConfigurationReadModelMetadataProvider();

        metadataProvider.Metadata.IndexName.Should().Be("gagent-service-configuration");
        metadataProvider.Metadata.Mappings.Should().BeEmpty();
        metadataProvider.Metadata.Settings.Should().BeEmpty();
        metadataProvider.Metadata.Aliases.Should().BeEmpty();
    }

    [Fact]
    public void AddGAgentServiceGovernanceProjection_ShouldRegisterGovernanceProjectionServices()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceGovernanceProjection();

        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionDocumentMetadataProvider<ServiceConfigurationReadModel>) &&
            x.ImplementationType == typeof(ServiceConfigurationReadModelMetadataProvider));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceConfigurationProjectionPort));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IServiceConfigurationQueryReader) &&
            x.ImplementationType == typeof(ServiceConfigurationQueryReader));
        services.Should().Contain(x =>
            x.ServiceType == typeof(IProjectionProjector<ServiceConfigurationProjectionContext, IReadOnlyList<string>>) &&
            x.ImplementationType == typeof(ServiceConfigurationProjector));
    }

    [Fact]
    public void GovernanceProjectionHelpers_ShouldResolveCommittedStateSupportBranches()
    {
        var assembly = typeof(ServiceConfigurationReadModelMetadataProvider).Assembly;
        var supportType = assembly.GetType("Aevatar.GAgentService.Governance.Projection.Internal.ServiceGovernanceCommittedStateSupport", throwOnError: true)!;
        var committedArgs = new object?[]
        {
            new EventEnvelope
            {
                Id = "outer-1",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-16T01:10:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-1",
                        Version = 8,
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
            .GetMethod("TryGetObservedPayload", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
            .Invoke(null, committedArgs)!;
        var plainArgs = new object?[]
        {
            new EventEnvelope
            {
                Id = "plain-1",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-16T03:00:00+00:00")),
                Payload = Any.Pack(new StringValue { Value = "plain" }),
            },
            new FixedProjectionClock(DateTimeOffset.Parse("2026-03-16T04:00:00+00:00")),
            null,
            null,
            null,
            null,
        };
        var plainResult = (bool)supportType
            .GetMethod("TryGetObservedPayload", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
            .Invoke(null, plainArgs)!;
        var invalidCommittedArgs = new object?[]
        {
            new EventEnvelope
            {
                Id = "outer-2",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-16T05:00:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-2",
                        Version = 0,
                    },
                }),
            },
            new FixedProjectionClock(DateTimeOffset.Parse("2026-03-16T06:00:00+00:00")),
            null,
            null,
            null,
            null,
        };
        var invalidCommittedResult = (bool)supportType
            .GetMethod("TryGetObservedPayload", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
            .Invoke(null, invalidCommittedArgs)!;
        var resolvedVersion = (long)supportType
            .GetMethod("ResolveNextStateVersion", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
            .Invoke(null, [0L, 0L])!;

        committedResult.Should().BeTrue();
        ((Any)committedArgs[2]!).Is(StringValue.Descriptor).Should().BeTrue();
        committedArgs[3].Should().Be("evt-1");
        committedArgs[4].Should().Be(8L);
        committedArgs[5].Should().Be(DateTimeOffset.Parse("2026-03-16T01:00:00+00:00"));
        plainResult.Should().BeTrue();
        ((Any)plainArgs[2]!).Is(StringValue.Descriptor).Should().BeTrue();
        plainArgs[3].Should().Be("plain-1");
        plainArgs[4].Should().Be(0L);
        plainArgs[5].Should().Be(DateTimeOffset.Parse("2026-03-16T03:00:00+00:00"));
        invalidCommittedResult.Should().BeTrue();
        ((Any)invalidCommittedArgs[2]!).Is(CommittedStateEventPublished.Descriptor).Should().BeTrue();
        invalidCommittedArgs[3].Should().Be("outer-2");
        invalidCommittedArgs[4].Should().Be(0L);
        invalidCommittedArgs[5].Should().Be(DateTimeOffset.Parse("2026-03-16T05:00:00+00:00"));
        resolvedVersion.Should().Be(1L);
    }

    [Fact]
    public void ConfigurationProjectionRuntimeLease_ShouldValidateContext()
    {
        Action act = () => new ServiceConfigurationRuntimeLease(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class RecordingConfigurationActivationService : IProjectionPortActivationService<ServiceConfigurationRuntimeLease>
    {
        public List<(string rootEntityId, string projectionName, string input, string commandId)> Calls { get; } = [];

        public Task<ServiceConfigurationRuntimeLease> EnsureAsync(
            string rootEntityId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            Calls.Add((rootEntityId, projectionName, input, commandId));
            return Task.FromResult(new ServiceConfigurationRuntimeLease(new ServiceConfigurationProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootEntityId}",
                RootActorId = rootEntityId,
            }));
        }
    }

    private sealed class RecordingConfigurationLifecycle : IProjectionLifecycleService<ServiceConfigurationProjectionContext, IReadOnlyList<string>>
    {
        public List<ServiceConfigurationProjectionContext> StartedContexts { get; } = [];

        public List<ServiceConfigurationProjectionContext> StoppedContexts { get; } = [];

        public Task StartAsync(ServiceConfigurationProjectionContext context, CancellationToken ct = default)
        {
            StartedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task ProjectAsync(ServiceConfigurationProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task StopAsync(ServiceConfigurationProjectionContext context, CancellationToken ct = default)
        {
            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(ServiceConfigurationProjectionContext context, IReadOnlyList<string> completion, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
