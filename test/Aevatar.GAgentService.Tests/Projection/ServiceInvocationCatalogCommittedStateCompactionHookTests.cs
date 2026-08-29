using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Projection.DependencyInjection;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ServiceInvocationCatalogCommittedStateCompactionHookTests
{
    [Fact]
    public void AddGAgentServiceProjection_ShouldRegisterCompactionBeforeProjectionActivationHook()
    {
        var services = new ServiceCollection();

        services.AddGAgentServiceProjection();

        var hooks = services
            .Where(static descriptor => descriptor.ServiceType == typeof(ICommittedStatePublicationHook))
            .Select(static descriptor => descriptor.ImplementationType)
            .ToArray();
        hooks.Should().Contain(typeof(ServiceInvocationCatalogCommittedStateCompactionHook));
        hooks.Should().Contain(typeof(CommittedStateProjectionActivationHook));
        Array.IndexOf(hooks, typeof(ServiceInvocationCatalogCommittedStateCompactionHook)).Should()
            .BeLessThan(Array.IndexOf(hooks, typeof(CommittedStateProjectionActivationHook)));
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldCompactLegacyRevisionPayloads_BeforeRecoveryRetry()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revision = LegacyRevision(identity, payloadBytes: 2_000_000);
        var state = new ServiceInvocationCatalogState
        {
            Identity = identity.Clone(),
            SourceCatalogVersion = 11,
            SourceServingVersion = 12,
            SourceRevisionVersion = 13,
            Revisions = { ["r1"] = revision.Clone() },
            Entries =
            {
                new ServiceInvocationCatalogEntryState
                {
                    EndpointId = "chat",
                    ReadinessStatus = ServiceInvokeReadinessStatus.Ready,
                    SelectedRevisionId = "r1",
                    SelectedDeploymentId = "dep-1",
                    SelectedActorId = "actor-1",
                },
            },
        };
        var published = new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                AgentId = "invocation-catalog-1",
                EventId = "evt-439",
                EventType = ServiceInvocationCatalogObservedEvent.Descriptor.FullName,
                Version = 439,
                EventData = Any.Pack(new ServiceInvocationCatalogObservedEvent
                {
                    Identity = identity.Clone(),
                    Revisions = { ["r1"] = revision.Clone() },
                }),
            },
            StateRoot = Any.Pack(state),
        };
        published.CalculateSize().Should().BeGreaterThan(4_000_000);

        await new ServiceInvocationCatalogCommittedStateCompactionHook().BeforePublishAsync(
            new CommittedStatePublicationContext
            {
                ActorId = "invocation-catalog-1",
                ActorType = typeof(ServiceInvocationCatalogGAgent),
                Published = published,
            },
            CancellationToken.None);

        published.CalculateSize().Should().BeLessThan(4_096);
        var compactState = published.StateRoot.Unpack<ServiceInvocationCatalogState>();
        compactState.Revisions.Should().BeEmpty();
        compactState.RevisionReadiness.Should().ContainSingle();
        compactState.RevisionReadiness["r1"].PreparedEndpointIds.Should().Equal("chat");
        compactState.Entries.Should().ContainSingle();
        compactState.SourceCatalogVersion.Should().Be(11);
        compactState.SourceServingVersion.Should().Be(12);
        compactState.SourceRevisionVersion.Should().Be(13);

        var compactEvent = published.StateEvent.EventData.Unpack<ServiceInvocationCatalogObservedEvent>();
        compactEvent.Revisions.Should().BeEmpty();
        compactEvent.RevisionReadiness.Should().ContainSingle();
    }

    private static ServiceRevisionRecordState LegacyRevision(ServiceIdentity identity, int payloadBytes)
    {
        var revision = new ServiceRevisionRecordState
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
            Status = ServiceRevisionStatus.Prepared,
            PreparedArtifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "r1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")),
        };
        revision.PreparedArtifact.ProtocolDescriptorSet = ByteString.CopyFrom(new byte[payloadBytes]);
        return revision;
    }
}
