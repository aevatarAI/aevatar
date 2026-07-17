using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.GAgentService.Tests.Projection;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogLifecycleTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public async Task CommandHandlers_ShouldPersistLifecycleAndIgnoreDuplicateOrStaleFacts()
    {
        var owner = Owner();
        var agent = CreateAgent(owner);
        var activatedAt = Timestamp.FromDateTimeOffset(ObservedAt);

        await agent.HandleActivateAsync(new ActivateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            ActivatedAt = activatedAt,
        });
        await agent.HandleActivateAsync(new ActivateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            ActivatedAt = activatedAt.Clone(),
        });

        var observation = ObservationCommand(owner, ObservedAt.AddMinutes(1));
        await agent.HandleObserveAsync(observation);
        await agent.HandleObserveAsync(observation.Clone());
        var staleObservation = ObservationCommand(owner, ObservedAt);
        await agent.HandleObserveAsync(staleObservation);

        var conflictingObservation = observation.Clone();
        conflictingObservation.ExternalRevision = "revision-conflict";
        var conflict = () => agent.HandleObserveAsync(conflictingObservation);
        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot identify conflicting content*");

        var refreshFailure = new RecordNyxIdAuthorizationCatalogRefreshFailureCommand
        {
            Owner = owner.Clone(),
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)),
            FailureCode = "provider_unavailable",
        };
        await agent.HandleRefreshFailureAsync(refreshFailure);
        await agent.HandleRefreshFailureAsync(refreshFailure.Clone());
        agent.State.LastRefreshFailureCode.Should().Be("provider_unavailable");

        var invalidation = new InvalidateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(3)),
            Reason = "credential_revoked",
        };
        await agent.HandleInvalidateAsync(invalidation);
        await agent.HandleInvalidateAsync(invalidation.Clone());
        agent.State.LifecycleFence.Should().Be(1);

        var cleanup = new CleanupNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            CleanedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(4)),
            Reason = "owner_unbound",
        };
        await agent.HandleCleanupAsync(cleanup);
        await agent.HandleCleanupAsync(cleanup.Clone());

        agent.State.Cleaned.Should().BeTrue();
        agent.State.CleanupReason.Should().Be("owner_unbound");
        agent.State.Activated.Should().BeFalse();
        agent.State.LifecycleFence.Should().Be(2);
    }

    [Theory]
    [InlineData("duplicate_service", "*service identities must be unique*")]
    [InlineData("binding_without_id", "*node authorization evidence is invalid*")]
    [InlineData("required_without_primary", "*require exactly one primary node*")]
    [InlineData("direct_with_nodes", "*cannot carry node authorization evidence*")]
    public async Task ObserveHandler_ShouldRejectInvalidTypedTopology(
        string scenario,
        string expectedMessage)
    {
        var owner = Owner();
        var agent = CreateAgent(owner);
        await agent.HandleActivateAsync(new ActivateNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            ActivatedAt = Timestamp.FromDateTimeOffset(ObservedAt),
        });
        var command = ObservationCommand(owner, ObservedAt.AddMinutes(1));

        switch (scenario)
        {
            case "duplicate_service":
                command.Services.Add(command.Services[0].Clone());
                break;
            case "binding_without_id":
                command.Services[0].Nodes[1].BindingId = string.Empty;
                break;
            case "required_without_primary":
                command.Services[0].Nodes.RemoveAt(0);
                break;
            case "direct_with_nodes":
                command.Services[0].NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
        command.ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
            command.Owner,
            command.Services);

        var act = () => agent.HandleObserveAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(expectedMessage);
    }

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
    public void ObservationFence_ShouldRejectResultStartedBeforeLifecycleChange()
    {
        var act = () => NyxIdAuthorizationCatalogGAgent.EnsureLifecycleFence(
            currentLifecycleFence: 4,
            expectedLifecycleFence: 3);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*superseded by a lifecycle change*");
        var current = () => NyxIdAuthorizationCatalogGAgent.EnsureLifecycleFence(4, 4);
        current.Should().NotThrow();
    }

    [Fact]
    public void CleanupTransition_ShouldClearCatalogFactsAndAdvanceFence()
    {
        var agent = new NyxIdAuthorizationCatalogGAgent();
        var owner = Owner();
        var observed = Transition(agent, new NyxIdAuthorizationCatalogState(), Observed(owner, "digest-1"));

        var cleaned = Transition(agent, observed, new NyxIdAuthorizationCatalogCleanedEvent
        {
            Owner = owner.Clone(),
            CleanedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)),
            Reason = "account_removed",
            LifecycleFence = 1,
        });

        cleaned.Owner.Should().BeEquivalentTo(owner);
        cleaned.Services.Should().BeEmpty();
        cleaned.ObservedAt.Should().BeNull();
        cleaned.FreshUntil.Should().BeNull();
        cleaned.ContentDigest.Should().BeEmpty();
        cleaned.Invalidated.Should().BeTrue();
        cleaned.Cleaned.Should().BeTrue();
        cleaned.CleanupReason.Should().Be("account_removed");
        cleaned.LifecycleFence.Should().Be(1);
    }

    [Fact]
    public async Task Projector_ShouldPublishNeverObservedInvalidationTombstone()
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
            new NyxIdAuthorizationCatalogInvalidatedEvent
            {
                Owner = owner.Clone(),
                InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt),
                Reason = "credential_revoked",
                LifecycleFence = 1,
            });

        await projector.ProjectAsync(
            new NyxIdAuthorizationCatalogProjectionContext
            {
                RootActorId = actorId,
                ProjectionKind = NyxIdAuthorizationCatalogGAgent.ProjectionKind,
            },
            CommittedEnvelope(state, 1, "evt-tombstone"));
        var snapshot = await new ProjectionNyxIdAuthorizationCatalogQueryPort(store).GetAsync(owner);

        snapshot.Should().NotBeNull();
        snapshot!.Invalidated.Should().BeTrue();
        snapshot.InvalidationReason.Should().Be("credential_revoked");
        snapshot.LifecycleFence.Should().Be(1);
        snapshot.Services.Should().BeEmpty();
        snapshot.ObservedAtUtc.Should().Be(default);
    }

    [Fact]
    public void ActorIds_ShouldIsolateAuthorityOwnerKindAndSubject()
    {
        var personal = Owner();
        var organization = personal.Clone();
        organization.OwnerKind = AuthorizationOwnerKind.Organization;
        var otherAuthority = personal.Clone();
        otherAuthority.Authority = "other-authority";
        var otherSubject = personal.Clone();
        otherSubject.OwnerSubject = "owner-beta";

        new[]
            {
                NyxIdAuthorizationCatalogActorIds.Build(personal),
                NyxIdAuthorizationCatalogActorIds.Build(organization),
                NyxIdAuthorizationCatalogActorIds.Build(otherAuthority),
                NyxIdAuthorizationCatalogActorIds.Build(otherSubject),
            }
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void CatalogPersistenceContracts_ShouldNotContainBearerOrTokenFields()
    {
        var persistedDescriptors = new[]
        {
            NyxIdAuthorizationCatalogState.Descriptor,
            NyxIdAuthorizationCatalogObservedEvent.Descriptor,
            NyxIdAuthorizationCatalogRefreshFailedEvent.Descriptor,
            NyxIdAuthorizationCatalogInvalidatedEvent.Descriptor,
            NyxIdAuthorizationCatalogCleanedEvent.Descriptor,
            NyxIdAuthorizationCatalogDocument.Descriptor,
        };

        persistedDescriptors
            .SelectMany(static descriptor => descriptor.Fields.InDeclarationOrder())
            .Select(static field => field.Name)
            .Should().NotContain(static name =>
                name.Contains("bearer", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("token", StringComparison.OrdinalIgnoreCase));
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

    private static NyxIdAuthorizationCatalogGAgent CreateAgent(AuthorizationOwnerIdentity owner) =>
        GAgentServiceTestKit.CreateStatefulAgent<
            NyxIdAuthorizationCatalogGAgent,
            NyxIdAuthorizationCatalogState>(
            new InMemoryEventStore(),
            NyxIdAuthorizationCatalogActorIds.Build(owner),
            static () => new NyxIdAuthorizationCatalogGAgent());

    private static ObserveNyxIdAuthorizationCatalogCommand ObservationCommand(
        AuthorizationOwnerIdentity owner,
        DateTimeOffset observedAt)
    {
        var command = new ObserveNyxIdAuthorizationCatalogCommand
        {
            Owner = owner.Clone(),
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(15)),
            ExternalRevision = "revision-1",
            ExpectedLifecycleFence = 0,
        };
        command.Services.Add(ServiceEvidence());
        command.ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
            command.Owner,
            command.Services);
        return command;
    }

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
        observed.Services.Add(ServiceEvidence());
        return observed;
    }

    private static NyxIdAuthorizationServiceEvidence ServiceEvidence()
    {
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
        return service;
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
