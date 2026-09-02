using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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
        var profileActorId = AgentProfileActorIds.Profile(state.Identity.ProfileId);
        var context = new AgentProfileCurrentStateProjectionContext
        {
            RootActorId = profileActorId,
            ProjectionKind = AgentProfileGAgent.DurableProjectionKind,
        };

        await new AgentProfileManagementCurrentStateProjector(managementStore, clock)
            .ProjectAsync(context, envelope);
        await new AgentProfileExecutionCurrentStateProjector(executionStore, clock)
            .ProjectAsync(context, envelope);

        var management = await new AgentProfileManagementQueryReader(managementStore)
            .GetAsync(state.Identity);
        var execution = await new AgentProfileExecutionQueryReader(executionStore)
            .GetAsync(new AgentProfileBindingTarget
            {
                Owner = state.Identity.Owner.Clone(),
                ProfileId = state.Identity.ProfileId,
                PublishedRevision = state.PublishedRevision,
                SnapshotSha256 = state.Published.SnapshotSha256,
            });

        management.Should().NotBeNull();
        management!.AuthorityStateVersion.Should().Be(7);
        management.DraftRevision.Should().Be(1);
        management.PublishedRevision.Should().Be(1);
        management.PublishedSnapshotSha256.Should().Equal(state.Published.SnapshotSha256);
        execution.Should().NotBeNull();
        execution!.AuthorityStateVersion.Should().Be(7);
        execution.Snapshot.RuntimeProfile.Instructions.Should().Be("Use cited sources.");
        execution.Snapshot.SnapshotSha256.Should().Equal(state.Published.SnapshotSha256);
    }

    [Fact]
    public void ManagementReadModel_ShouldContainOnlyPublishedSummary()
    {
        AgentProfileManagementReadModel.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Equal(
                "id",
                "actor_id",
                "state_version",
                "last_event_id",
                "updated_at_utc_value",
                "identity",
                "draft",
                "draft_revision",
                "draft_sha256",
                "published_display_name",
                "published_purpose",
                "published_revision",
                "published_snapshot_sha256",
                "published_at",
                "last_mutation");
    }

    [Fact]
    public async Task TypedProfileReaders_ShouldRejectWrongOwnerRevisionOrDigest()
    {
        var managementStore = new RecordingDocumentStore<AgentProfileManagementReadModel>(x => x.Id);
        var executionStore = new RecordingDocumentStore<AgentProfileExecutionReadModel>(x => x.Id);
        var state = PublishedState();
        var actorId = AgentProfileActorIds.Profile(state.Identity.ProfileId);
        var context = new AgentProfileCurrentStateProjectionContext
        {
            RootActorId = actorId,
            ProjectionKind = AgentProfileGAgent.DurableProjectionKind,
        };
        var envelope = Wrap(state, 7, "evt-profile-identity-7");
        var clock = new FixedProjectionClock(DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
        await new AgentProfileManagementCurrentStateProjector(managementStore, clock)
            .ProjectAsync(context, envelope);
        await new AgentProfileExecutionCurrentStateProjector(executionStore, clock)
            .ProjectAsync(context, envelope);

        var wrongIdentity = state.Identity.Clone();
        wrongIdentity.Owner = AgentProfileOwners.ForScope("scope-other");
        var management = await new AgentProfileManagementQueryReader(managementStore)
            .GetAsync(wrongIdentity);
        var wrongOwner = Target(state, owner: AgentProfileOwners.ForScope("scope-other"));
        var wrongRevision = Target(state, publishedRevision: 2);
        var wrongDigest = Target(state, digestByte: 0x7f);
        var reader = new AgentProfileExecutionQueryReader(executionStore);

        management.Should().BeNull();
        (await reader.GetAsync(wrongOwner)).Should().BeNull();
        (await reader.GetAsync(wrongRevision)).Should().BeNull();
        (await reader.GetAsync(wrongDigest)).Should().BeNull();
    }

    [Fact]
    public async Task CatalogReader_ShouldRejectDocumentOwnedByDifferentNamespace()
    {
        var store = new RecordingDocumentStore<AgentProfileCatalogReadModel>(x => x.Id);
        var requestedOwner = AgentProfileOwners.ForScope("scope-gamma");
        var document = new AgentProfileCatalogReadModel
        {
            Id = AgentProfileActorIds.Namespace(requestedOwner),
            ActorId = AgentProfileActorIds.Namespace(requestedOwner),
            StateVersion = 1,
            Owner = AgentProfileOwners.ForScope("scope-other"),
        };
        await store.UpsertAsync(document);

        var result = await new AgentProfileCatalogQueryReader(store).GetAsync(requestedOwner);

        result.Should().BeNull();
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
            Target = new AgentProfileBindingTarget
            {
                Owner = owner.Clone(),
                ProfileId = "prof-alpha",
                PublishedRevision = 1,
                SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x22, 32).ToArray()),
            },
            Scope = new AgentProfileScopeBindingAdmission(),
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

    [Fact]
    public async Task ProfileCurrentStateProjectors_ShouldKeepNewerAuthorityVersionWhenOlderEventArrives()
    {
        var managementStore = new RecordingDocumentStore<AgentProfileManagementReadModel>(x => x.Id)
        {
            EnforceMonotonicWrites = true,
        };
        var executionStore = new RecordingDocumentStore<AgentProfileExecutionReadModel>(x => x.Id)
        {
            EnforceMonotonicWrites = true,
        };
        var state = PublishedState();
        var context = ProfileContext(state);
        var clock = new FixedProjectionClock(DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
        var managementProjector = new AgentProfileManagementCurrentStateProjector(managementStore, clock);
        var executionProjector = new AgentProfileExecutionCurrentStateProjector(executionStore, clock);
        await managementProjector.ProjectAsync(context, Wrap(state, 8, "evt-profile-8"));
        await executionProjector.ProjectAsync(context, Wrap(state, 8, "evt-profile-8"));

        var stale = state.Clone();
        stale.DraftRevision = 99;
        stale.Published.RuntimeProfile.Instructions = "stale instructions";
        await managementProjector.ProjectAsync(context, Wrap(stale, 7, "evt-profile-7"));
        await executionProjector.ProjectAsync(context, Wrap(stale, 7, "evt-profile-7"));

        var management = await managementStore.GetAsync(context.RootActorId);
        var execution = await executionStore.GetAsync(context.RootActorId);
        management!.StateVersion.Should().Be(8);
        management.DraftRevision.Should().Be(1);
        execution!.StateVersion.Should().Be(8);
        execution.Snapshot.RuntimeProfile.Instructions.Should().Be("Use cited sources.");
    }

    [Fact]
    public async Task ProfileCurrentStateProjectors_ShouldRejectConflictingSameVersion()
    {
        var managementStore = new RecordingDocumentStore<AgentProfileManagementReadModel>(x => x.Id)
        {
            EnforceMonotonicWrites = true,
        };
        var executionStore = new RecordingDocumentStore<AgentProfileExecutionReadModel>(x => x.Id)
        {
            EnforceMonotonicWrites = true,
        };
        var state = PublishedState();
        var context = ProfileContext(state);
        var clock = new FixedProjectionClock(DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
        var managementProjector = new AgentProfileManagementCurrentStateProjector(managementStore, clock);
        var executionProjector = new AgentProfileExecutionCurrentStateProjector(executionStore, clock);
        await managementProjector.ProjectAsync(context, Wrap(state, 7, "evt-profile-7-a"));
        await executionProjector.ProjectAsync(context, Wrap(state, 7, "evt-profile-7-a"));

        var conflicting = state.Clone();
        conflicting.DraftRevision = 99;
        conflicting.Published.RuntimeProfile.Instructions = "conflicting instructions";
        Func<Task> writeManagement = () => managementProjector
            .ProjectAsync(context, Wrap(conflicting, 7, "evt-profile-7-b"))
            .AsTask();
        Func<Task> writeExecution = () => executionProjector
            .ProjectAsync(context, Wrap(conflicting, 7, "evt-profile-7-b"))
            .AsTask();

        await writeManagement.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Conflict*");
        await writeExecution.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Conflict*");
    }

    [Fact]
    public async Task CatalogCurrentStateProjector_ShouldRejectConflictingSameVersion()
    {
        var store = new RecordingDocumentStore<AgentProfileCatalogReadModel>(x => x.Id)
        {
            EnforceMonotonicWrites = true,
        };
        var owner = AgentProfileOwners.ForScope("scope-gamma");
        var context = new AgentProfileCatalogProjectionContext
        {
            RootActorId = AgentProfileActorIds.Namespace(owner),
            ProjectionKind = AgentProfileNamespaceGAgent.DurableProjectionKind,
        };
        var projector = new AgentProfileCatalogCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-30T00:00:00Z")));
        var state = new AgentProfileNamespaceState { Owner = owner.Clone() };
        state.Profiles.Add(new AgentProfileCatalogEntry
        {
            ProfileId = "prof-alpha",
            ProfileSlug = "research-assistant",
        });
        await projector.ProjectAsync(context, Wrap(state, 4, "evt-namespace-4-a"));

        var conflicting = state.Clone();
        conflicting.Profiles[0].ProfileSlug = "conflicting-slug";
        Func<Task> writeConflict = () => projector
            .ProjectAsync(context, Wrap(conflicting, 4, "evt-namespace-4-b"))
            .AsTask();

        await writeConflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Conflict*");
    }

    [Theory]
    [InlineData(typeof(AgentProfileCatalogQueryReader))]
    [InlineData(typeof(AgentProfileManagementQueryReader))]
    [InlineData(typeof(AgentProfileExecutionQueryReader))]
    public void AgentProfileQueryReaders_ShouldDependOnlyOnDocumentReader(System.Type readerType)
    {
        var constructor = readerType.GetConstructors().Should().ContainSingle().Subject;
        var parameter = constructor.GetParameters().Should().ContainSingle().Subject;

        parameter.ParameterType.IsGenericType.Should().BeTrue();
        parameter.ParameterType.GetGenericTypeDefinition()
            .Should().Be(typeof(IProjectionDocumentReader<,>));
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

    private static AgentProfileCurrentStateProjectionContext ProfileContext(AgentProfileState state) => new()
    {
        RootActorId = AgentProfileActorIds.Profile(state.Identity.ProfileId),
        ProjectionKind = AgentProfileGAgent.DurableProjectionKind,
    };

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

    private static AgentProfileBindingTarget Target(
        AgentProfileState state,
        AgentProfileOwner? owner = null,
        long? publishedRevision = null,
        byte? digestByte = null) => new()
    {
        Owner = (owner ?? state.Identity.Owner).Clone(),
        ProfileId = state.Identity.ProfileId,
        PublishedRevision = publishedRevision ?? state.PublishedRevision,
        SnapshotSha256 = digestByte.HasValue
            ? ByteString.CopyFrom(Enumerable.Repeat(digestByte.Value, 32).ToArray())
            : state.Published.SnapshotSha256,
    };
}
