using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.Projection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogLifecycleTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public void StateTransition_ShouldPreserveCatalogFactsAndClearInvalidationOnNewObservation()
    {
        var agent = new NyxIdAuthorizationCatalogGAgent();
        var owner = Owner();
        var initial = Transition(agent, new NyxIdAuthorizationCatalogState(), Observed(owner, "digest-1"));
        var failed = Transition(agent, initial, new NyxIdAuthorizationCatalogRefreshFailedEvent
        {
            Owner = owner.Clone(),
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
            FailureCode = "provider_unavailable",
        });
        var invalidated = Transition(agent, failed, new NyxIdAuthorizationCatalogInvalidatedEvent
        {
            Owner = owner.Clone(),
            InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)),
            Reason = "credential_revoked",
        });
        var refreshed = Transition(agent, invalidated, Observed(owner, "digest-2"));

        failed.ContentDigest.Should().Be("digest-1");
        failed.Services.Should().ContainSingle();
        failed.LastRefreshFailureCode.Should().Be("provider_unavailable");
        invalidated.Invalidated.Should().BeTrue();
        invalidated.InvalidationReason.Should().Be("credential_revoked");
        refreshed.Invalidated.Should().BeFalse();
        refreshed.InvalidationReason.Should().BeEmpty();
        refreshed.ContentDigest.Should().Be("digest-2");
        refreshed.LastRefreshFailureCode.Should().Be("provider_unavailable");
    }

    [Fact]
    public async Task ProjectorAndQuery_ShouldRoundTripOwnerScopedCommittedState()
    {
        var owner = Owner();
        var actorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
        var store = new RecordingDocumentStore<NyxIdAuthorizationCatalogDocument>(static document => document.Id);
        var projector = new NyxIdAuthorizationCatalogCurrentStateProjector(
            store,
            new FixedProjectionClock(ObservedAt.AddMinutes(3)));
        var state = Transition(
            new NyxIdAuthorizationCatalogGAgent(),
            new NyxIdAuthorizationCatalogState(),
            Observed(owner, "digest-19"));

        await projector.ProjectAsync(
            new NyxIdAuthorizationCatalogProjectionContext
            {
                RootActorId = actorId,
                ProjectionKind = NyxIdAuthorizationCatalogGAgent.ProjectionKind,
            },
            CommittedEnvelope(state, 19, "evt-19"));
        var snapshot = await new ProjectionNyxIdAuthorizationCatalogQueryPort(store).GetAsync(owner);

        snapshot.Should().NotBeNull();
        snapshot!.Owner.Should().BeEquivalentTo(owner);
        snapshot.StateVersion.Should().Be(19);
        snapshot.ContentDigest.Should().Be("digest-19");
        var service = snapshot.Services.Should().ContainSingle().Subject;
        service.UserServiceId.Should().Be("svc-alpha");
        service.Nodes.Select(static node => (node.NodeId, node.BindingId, node.RoutePriority))
            .Should().Equal(
                ("node-primary", string.Empty, 0),
                ("node-fallback", "binding-a", 7),
                ("node-fallback", "binding-b", 7));
    }

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "owner-alpha",
    };

    private static NyxIdAuthorizationCatalogObservedEvent Observed(
        AuthorizationOwnerIdentity owner,
        string digest)
    {
        var observed = new NyxIdAuthorizationCatalogObservedEvent
        {
            Owner = owner.Clone(),
            ObservedAt = Timestamp.FromDateTimeOffset(ObservedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(15)),
            ExternalRevision = "revision-1",
            ContentDigest = digest,
        };
        var service = new NyxIdAuthorizationServiceEvidence
        {
            UserServiceId = "svc-alpha",
            ServiceSlug = "calendar",
            DisplayName = "Calendar",
            Access = NyxIdAuthorizationAccess.Permitted,
            NodeGrantRequirement = AuthorizationGrantRequirement.Required,
        };
        service.Nodes.Add(new NyxIdAuthorizationNodeEvidence
        {
            NodeId = "node-primary",
            DisplayName = "Primary",
            Role = NyxIdNodeRole.Primary,
            EdgeKind = NyxIdNodeEdgeKind.UserServicePrimary,
        });
        service.Nodes.Add(new NyxIdAuthorizationNodeEvidence
        {
            NodeId = "node-fallback",
            DisplayName = "Fallback",
            Role = NyxIdNodeRole.Fallback,
            EdgeKind = NyxIdNodeEdgeKind.NodeBinding,
            BindingId = "binding-a",
            RoutePriority = 7,
        });
        service.Nodes.Add(new NyxIdAuthorizationNodeEvidence
        {
            NodeId = "node-fallback",
            DisplayName = "Fallback",
            Role = NyxIdNodeRole.Fallback,
            EdgeKind = NyxIdNodeEdgeKind.NodeBinding,
            BindingId = "binding-b",
            RoutePriority = 7,
        });
        observed.Services.Add(service);
        return observed;
    }

    private static NyxIdAuthorizationCatalogState Transition(
        NyxIdAuthorizationCatalogGAgent agent,
        NyxIdAuthorizationCatalogState state,
        IMessage evt)
    {
        var method = typeof(NyxIdAuthorizationCatalogGAgent).GetMethod(
            "TransitionState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TransitionState method not found.");
        return (NyxIdAuthorizationCatalogState)method.Invoke(agent, [state, evt])!;
    }

    private static EventEnvelope CommittedEnvelope(
        NyxIdAuthorizationCatalogState state,
        long version,
        string eventId) => new()
    {
        Id = eventId,
        Timestamp = Timestamp.FromDateTimeOffset(ObservedAt),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = eventId,
                Version = version,
                EventData = Any.Pack(Observed(Owner(), "digest-19")),
                Timestamp = Timestamp.FromDateTimeOffset(ObservedAt),
            },
            StateRoot = Any.Pack(state),
        }),
    };
}
