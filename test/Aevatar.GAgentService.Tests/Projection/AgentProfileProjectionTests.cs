using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class AgentProfileProjectionTests
{
    [Fact]
    public async Task CommittedProfileState_ShouldFanOutManagementAndProtectedExecution()
    {
        var managementStore = new RecordingDocumentStore<AgentProfileManagementReadModel>(x => x.Id);
        var executionStore = new RecordingDocumentStore<AgentProfileExecutionReadModel>(x => x.Id);
        var clock = new FixedProjectionClock(DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
        var state = PublishedState();
        var envelope = Wrap(state, 7, "evt-profile-7");
        var context = new AgentProfileCurrentStateProjectionContext
        {
            RootActorId = "profile-actor-alpha",
            ProjectionKind = AgentProfileGAgent.DurableProjectionKind,
        };

        await new AgentProfileManagementCurrentStateProjector(managementStore, clock)
            .ProjectAsync(context, envelope);
        await new AgentProfileExecutionCurrentStateProjector(executionStore, clock)
            .ProjectAsync(context, envelope);

        var management = await new AgentProfileManagementQueryReader(managementStore)
            .GetAsync("profile-actor-alpha");
        var execution = await new AgentProfileExecutionQueryReader(executionStore)
            .GetAsync("profile-actor-alpha");

        management.Should().NotBeNull();
        management!.AuthorityStateVersion.Should().Be(7);
        management.DraftRevision.Should().Be(1);
        management.PublishedRevision.Should().Be(1);
        execution.Should().NotBeNull();
        execution!.AuthorityStateVersion.Should().Be(7);
        execution.Snapshot.RuntimeProfile.Instructions.Should().Be("Use cited sources.");
        execution.Snapshot.SnapshotSha256.Should().Equal(state.Published.SnapshotSha256);
    }

    [Fact]
    public async Task CommittedNamespaceState_ShouldMaterializeCatalogAndBinding()
    {
        var store = new RecordingDocumentStore<AgentProfileCatalogReadModel>(x => x.Id);
        var projector = new AgentProfileCatalogCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-30T00:00:00Z")));
        var owner = AgentProfileOwners.ForScope("scope-gamma");
        var state = new AgentProfileNamespaceState { Owner = owner.Clone() };
        state.Profiles.Add(new AgentProfileCatalogEntry
        {
            ProfileId = "prof-alpha",
            ProfileSlug = "research-assistant",
            ProfileActorId = "profile-actor-alpha",
            Status = AgentProfileProvisioningStatus.Active,
            PublishedRevision = 1,
            SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x22, 32).ToArray()),
        });
        state.DefaultBindings.Add(new AgentProfileDefaultBinding
        {
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            ProfileId = "prof-alpha",
            Enabled = true,
            CohortBasisPoints = 10_000,
        });

        var namespaceActorId = AgentProfileActorIds.Namespace(owner);
        await projector.ProjectAsync(
            new AgentProfileCatalogProjectionContext
            {
                RootActorId = namespaceActorId,
                ProjectionKind = AgentProfileNamespaceGAgent.DurableProjectionKind,
            },
            Wrap(state, 4, "evt-namespace-4"));

        var snapshot = await new AgentProfileCatalogQueryReader(store).GetAsync(owner);
        snapshot.Should().NotBeNull();
        snapshot!.AuthorityStateVersion.Should().Be(4);
        snapshot.Profiles.Should().ContainSingle(x => x.ProfileSlug == "research-assistant");
        snapshot.DefaultBindings.Should().ContainSingle(x => x.AgentKind == AgentProfilePolicies.NyxIdChatAgentKind);
    }

    private static AgentProfileState PublishedState()
    {
        var identity = new AgentProfileIdentity
        {
            ProfileId = "prof-alpha",
            Owner = AgentProfileOwners.ForScope("scope-gamma"),
            ProfileSlug = "research-assistant",
        };
        var draft = new AgentProfileDraft
        {
            DisplayName = "Research Assistant",
            Purpose = "Research public sources",
            Instructions = "Use cited sources.",
            RuntimeProfile = new AgentProfileSnapshot
            {
                AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
                RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
                ActivationMode = AgentProfileActivationMode.Enforced,
                MaximumToolPolicy = new AgentProfileToolPolicy(),
                RecoveryToolPolicy = new AgentProfileToolPolicy(),
            },
        };
        var published = AgentProfileDeterminism.BuildPublishedSnapshot(
            identity,
            draft,
            1,
            1,
            DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
        return new AgentProfileState
        {
            Identity = identity,
            NamespaceActorId = "namespace-actor-alpha",
            Draft = draft,
            DraftRevision = 1,
            DraftSha256 = AgentProfileDeterminism.ComputeDraftDigest(draft),
            Published = published,
            PublishedRevision = 1,
        };
    }

    private static EventEnvelope Wrap(IMessage state, long version, string eventId) => new()
    {
        Id = $"outer-{eventId}",
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-30T00:00:00Z")),
        Route = EnvelopeRouteSemantics.CreateObserverPublication("root-actor"),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = eventId,
                Version = version,
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-30T00:00:00Z")),
                EventData = Any.Pack(new AgentProfileStateChangedEvent()),
            },
            StateRoot = Any.Pack(state),
        }),
    };
}
