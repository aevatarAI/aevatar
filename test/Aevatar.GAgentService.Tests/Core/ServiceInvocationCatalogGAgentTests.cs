using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Services;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceInvocationCatalogGAgentTests
{
    [Fact]
    public async Task Observations_ShouldConvergeToReady_InAnyOrder()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(identity);

        await agent.HandleServingObservationAsync(new ObserveServiceInvocationServingCommand
        {
            Identity = identity.Clone(),
            ServingTargets = { Target("dep-1", "r1", "actor-1", "chat") },
            SourceServingVersion = 2,
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-05T00:02:00+00:00")),
        });
        await agent.HandleRevisionObservationAsync(new ObserveServiceInvocationRevisionsCommand
        {
            Identity = identity.Clone(),
            Revisions = { { "r1", PreparedRevision(identity, "r1", "chat") } },
            SourceRevisionVersion = 3,
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-05T00:03:00+00:00")),
        });
        await agent.HandleCatalogObservationAsync(new ObserveServiceInvocationCatalogCommand
        {
            Identity = identity.Clone(),
            ServiceEndpoints = { GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat") },
            SourceCatalogVersion = 1,
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-05T00:01:00+00:00")),
        });

        agent.State.Entries.Should().ContainSingle();
        agent.State.Entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Ready);
        agent.State.Entries[0].UnavailableReason.Should().Be(ServiceInvokeUnavailableReason.Unspecified);
        agent.State.SourceCatalogVersion.Should().Be(1);
        agent.State.SourceServingVersion.Should().Be(2);
        agent.State.SourceRevisionVersion.Should().Be(3);
    }

    [Theory]
    [InlineData(ServiceInvokeUnavailableReason.ServingTargetMissing)]
    [InlineData(ServiceInvokeUnavailableReason.RevisionNotPrepared)]
    [InlineData(ServiceInvokeUnavailableReason.PreparedArtifactMissing)]
    public async Task Observations_ShouldProduceCanonicalUnavailableReasons(ServiceInvokeUnavailableReason reason)
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(identity);

        await agent.HandleCatalogObservationAsync(new ObserveServiceInvocationCatalogCommand
        {
            Identity = identity.Clone(),
            ServiceEndpoints = { GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat") },
            SourceCatalogVersion = 1,
        });

        if (reason != ServiceInvokeUnavailableReason.ServingTargetMissing)
        {
            await agent.HandleServingObservationAsync(new ObserveServiceInvocationServingCommand
            {
                Identity = identity.Clone(),
                ServingTargets = { Target("dep-1", "r1", "actor-1", "chat") },
                SourceServingVersion = 1,
            });
        }

        if (reason == ServiceInvokeUnavailableReason.RevisionNotPrepared)
        {
            await agent.HandleRevisionObservationAsync(new ObserveServiceInvocationRevisionsCommand
            {
                Identity = identity.Clone(),
                Revisions = { { "r1", new ServiceRevisionRecordState { Status = ServiceRevisionStatus.Created } } },
                SourceRevisionVersion = 1,
            });
        }
        else if (reason == ServiceInvokeUnavailableReason.PreparedArtifactMissing)
        {
            await agent.HandleRevisionObservationAsync(new ObserveServiceInvocationRevisionsCommand
            {
                Identity = identity.Clone(),
                Revisions = { { "r1", PreparedRevision(identity, "r1", "other") } },
                SourceRevisionVersion = 1,
            });
        }

        agent.State.Entries.Should().ContainSingle();
        agent.State.Entries[0].ReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable);
        agent.State.Entries[0].UnavailableReason.Should().Be(reason);
    }

    [Fact]
    public async Task Observation_ShouldIgnoreOlderSourceVersion()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateAgent(identity);

        await agent.HandleCatalogObservationAsync(new ObserveServiceInvocationCatalogCommand
        {
            Identity = identity.Clone(),
            ServiceEndpoints = { GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat") },
            SourceCatalogVersion = 2,
        });
        await agent.HandleCatalogObservationAsync(new ObserveServiceInvocationCatalogCommand
        {
            Identity = identity.Clone(),
            ServiceEndpoints = { GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "stale") },
            SourceCatalogVersion = 1,
        });

        agent.State.SourceCatalogVersion.Should().Be(2);
        agent.State.ServiceEndpoints.Should().ContainSingle(x => x.EndpointId == "chat");
    }

    [Fact]
    public async Task RevisionObservation_ShouldRetainOnlyBoundedReadinessState()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revision = PreparedRevision(identity, "r1", "chat");
        revision.PreparedArtifact.ProtocolDescriptorSet = ByteString.CopyFrom(new byte[2_000_000]);
        var agent = CreateAgent(identity);

        await agent.HandleRevisionObservationAsync(new ObserveServiceInvocationRevisionsCommand
        {
            Identity = identity.Clone(),
            Revisions = { ["r1"] = revision },
            SourceRevisionVersion = 3,
        });

        agent.State.Revisions.Should().BeEmpty();
        agent.State.RevisionReadiness.Should().ContainSingle();
        agent.State.RevisionReadiness["r1"].PreparedEndpointIds.Should().Equal("chat");
        agent.State.CalculateSize().Should().BeLessThan(4_096);
    }

    private static ServiceInvocationCatalogGAgent CreateAgent(ServiceIdentity identity) =>
        GAgentServiceTestKit.CreateStatefulAgent<ServiceInvocationCatalogGAgent, ServiceInvocationCatalogState>(
            new InMemoryEventStore(),
            ServiceActorIds.InvocationCatalog(identity),
            static () => new ServiceInvocationCatalogGAgent(new ServiceInvokeReadinessEvaluator()));

    private static ServiceServingTargetSpec Target(
        string deploymentId,
        string revisionId,
        string actorId,
        params string[] endpointIds)
    {
        var target = new ServiceServingTargetSpec
        {
            DeploymentId = deploymentId,
            RevisionId = revisionId,
            PrimaryActorId = actorId,
            AllocationWeight = 100,
            ServingState = ServiceServingState.Active,
        };
        target.EnabledEndpointIds.Add(endpointIds);
        return target;
    }

    private static ServiceRevisionRecordState PreparedRevision(
        ServiceIdentity identity,
        string revisionId,
        params string[] endpointIds) =>
        new()
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, revisionId),
            Status = ServiceRevisionStatus.Prepared,
            PreparedArtifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                revisionId,
                endpointIds
                    .Select(endpointId => GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: endpointId))
                    .ToArray()),
        };
}
