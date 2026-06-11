using Aevatar.CQRS.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.Orchestration;
using Aevatar.GAgentService.Governance.Projection.DependencyInjection;
using Aevatar.GAgentService.Governance.Projection.Metadata;
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
            x.ServiceType == typeof(IServiceConfigurationQueryReader) &&
            x.ImplementationType == typeof(ServiceConfigurationQueryReader));
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IProjectionMaterializer<ServiceConfigurationProjectionContext>) &&
            IsObservedProjectionMaterializerFor<ServiceConfigurationProjector>(x.ImplementationType));
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IProjectionArtifactMaterializer<ServiceConfigurationProjectionContext>) &&
            IsObservedProjectionArtifactMaterializerFor<ServiceConfigurationProjector>(x.ImplementationType));
    }

    private static bool IsObservedProjectionMaterializerFor<TProjector>(System.Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedProjectionMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TProjector);
    }

    private static bool IsObservedProjectionArtifactMaterializerFor<TProjector>(System.Type? type)
    {
        return type?.IsGenericType == true &&
               type.Name.StartsWith("ObservedProjectionArtifactMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TProjector);
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
        committedResult.Should().BeTrue();
        ((Any)committedArgs[2]!).Is(StringValue.Descriptor).Should().BeTrue();
        committedArgs[3].Should().Be("evt-1");
        committedArgs[4].Should().Be(8L);
        committedArgs[5].Should().Be(DateTimeOffset.Parse("2026-03-16T01:00:00+00:00"));
        plainResult.Should().BeFalse();
        plainArgs[2].Should().BeNull();
        plainArgs[3].Should().Be(string.Empty);
        plainArgs[4].Should().Be(0L);
        plainArgs[5].Should().Be(default(DateTimeOffset));
        invalidCommittedResult.Should().BeFalse();
        invalidCommittedArgs[2].Should().BeNull();
        invalidCommittedArgs[3].Should().Be(string.Empty);
        invalidCommittedArgs[4].Should().Be(0L);
        invalidCommittedArgs[5].Should().Be(default(DateTimeOffset));
    }

    [Fact]
    public void ConfigurationProjectionRuntimeLease_ShouldValidateContext()
    {
        Action act = () => new ServiceConfigurationRuntimeLease(null!);

        act.Should().Throw<ArgumentNullException>();
    }

}
