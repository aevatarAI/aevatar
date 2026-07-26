using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Projection.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class AgentProfileCurrentStateProjectorTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-07-23T01:02:03+00:00");

    [Fact]
    public async Task Projectors_ShouldIgnoreNonCommittedEnvelopesAndWrongStateRoots()
    {
        var namespaceStore = new RecordingDocumentStore<AgentProfileNamespaceCatalogDocument>(x => x.Id);
        var ownerStore = new RecordingDocumentStore<AgentProfileOwnerDocument>(x => x.Id);
        var executionStore = new RecordingDocumentStore<AgentProfileExecutionDocument>(x => x.Id);
        var namespaceProjector = new AgentProfileNamespaceCurrentStateProjector(
            namespaceStore,
            new FixedProjectionClock(ObservedAt));
        var ownerProjector = new AgentProfileOwnerCurrentStateProjector(
            ownerStore,
            new FixedProjectionClock(ObservedAt));
        var executionProjector = new AgentProfileExecutionCurrentStateProjector(
            executionStore,
            new FixedProjectionClock(ObservedAt));
        var nonCommitted = new EventEnvelope
        {
            Id = "not-committed",
            Payload = Any.Pack(new StringValue { Value = "raw" }),
        };
        var wrongStateRoot = WrapCommitted(
            new StringValue { Value = "wrong-root" },
            stateVersion: 1,
            eventId: "evt-wrong-root");

        await namespaceProjector.ProjectAsync(NamespaceContext(), nonCommitted);
        await namespaceProjector.ProjectAsync(NamespaceContext(), wrongStateRoot);
        await ownerProjector.ProjectAsync(OwnerContext(), nonCommitted);
        await ownerProjector.ProjectAsync(OwnerContext(), wrongStateRoot);
        await executionProjector.ProjectAsync(ExecutionContext(), nonCommitted);
        await executionProjector.ProjectAsync(ExecutionContext(), wrongStateRoot);

        (await namespaceStore.ReadItemsAsync()).Should().BeEmpty();
        (await ownerStore.ReadItemsAsync()).Should().BeEmpty();
        (await executionStore.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task NamespaceProjector_ShouldMaterializeOnlyActiveSafeCatalogEntries()
    {
        var store = new RecordingDocumentStore<AgentProfileNamespaceCatalogDocument>(x => x.Id);
        var projector = new AgentProfileNamespaceCurrentStateProjector(
            store,
            new FixedProjectionClock(ObservedAt));
        var state = new AgentProfileNamespaceState();
        state.Profiles.Add(NamespaceEntry(
            "profile-active",
            "alpha",
            AgentProfileProvisioningStatus.Active,
            published: true));
        state.Profiles.Add(NamespaceEntry(
            "profile-provisioning",
            "beta",
            AgentProfileProvisioningStatus.Provisioning,
            published: false));
        state.Profiles.Add(NamespaceEntry(
            "profile-failed",
            "gamma",
            AgentProfileProvisioningStatus.Failed,
            published: false));

        await projector.ProjectAsync(
            NamespaceContext(),
            WrapCommitted(state, stateVersion: 41, eventId: "evt-namespace-41"));

        var document = await store.GetAsync(AgentProfileActorIds.Namespace);
        document.Should().NotBeNull();
        document!.ActorId.Should().Be(AgentProfileActorIds.Namespace);
        document.StateVersion.Should().Be(41);
        document.LastEventId.Should().Be("evt-namespace-41");
        document.Entries.Should().ContainSingle();
        var entry = document.Entries.Single();
        entry.ProfileId.Should().Be("profile-active");
        entry.Reference.ProfileSlug.Should().Be("alpha");
        entry.Owner.User.SubjectId.Should().Be("owner-subject");
        entry.OwningScopeId.Should().Be("scope-owner");
        entry.Status.Should().Be(AgentProfileProvisioningStatus.Active);
        entry.PublishedSummary.Should().NotBeNull();
        entry.PublishedSummary.DisplayName.Should().Be("Published alpha");
        entry.PublishedSummary.Purpose.Should().Be("Safe summary alpha");
        document.ToString().Should().NotContain("draft-secret");
        typeof(AgentProfileCatalogEntryDocument).GetProperty("InitialContent").Should().BeNull();
        typeof(AgentProfileCatalogEntryDocument).GetProperty("Failure").Should().BeNull();
        typeof(AgentProfileCatalogEntryDocument).GetProperty("ProfileActorId").Should().BeNull();
    }

    [Fact]
    public async Task OwnerProjector_ShouldMaterializeManagementSafeViewWithoutSealedSnapshot()
    {
        var store = new RecordingDocumentStore<AgentProfileOwnerDocument>(x => x.Id);
        var projector = new AgentProfileOwnerCurrentStateProjector(
            store,
            new FixedProjectionClock(ObservedAt));
        var state = ProfileState(published: true);

        await projector.ProjectAsync(
            OwnerContext(),
            WrapCommitted(state, stateVersion: 42, eventId: "evt-profile-42"));

        var written = await store.GetAsync("profile-alpha");
        written.Should().NotBeNull();
        written!.Id.Should().Be("profile-alpha");
        written.ActorId.Should().Be("profile-actor-alpha");
        written.StateVersion.Should().Be(42);
        written.LastEventId.Should().Be("evt-profile-42");
        written.Identity.Reference.ProfileSlug.Should().Be("alpha");
        written.Draft.DisplayName.Should().Be("Draft alpha");
        written.Draft.Instructions.Should().Be("draft instructions");
        written.Draft.SkillBindings.Should().ContainSingle();
        written.Draft.SkillBindings[0].Skill.LiteralVersion.Should().Be("1.2");
        written.Draft.ToolPolicy.Mode.Should().Be(AgentProfileToolPolicyMode.ExplicitAllowlist);
        written.Draft.ToolPolicy.ToolNames.Should().Equal("calendar.read");
        written.DraftRevision.Should().Be(6);
        written.DraftSha256.Should().Equal(ByteString.CopyFromUtf8("draft-sha-6"));
        written.PublishedRevision.Should().Be(5);
        written.PublishedSnapshotSha256.Should().Equal(ByteString.CopyFromUtf8("snapshot-sha-5"));
        written.PublishedSourceDraftSha256.Should().Equal(ByteString.CopyFromUtf8("source-draft-sha-5"));
        written.LastMutation.Status.Should().Be(AgentProfileMutationStatus.Applied);
        written.LastMutation.Operation.OperationId.Should().Be("operation-42");
        written.ToString().Should().NotContain("sealed skill body");
        typeof(AgentProfileOwnerDocument).GetProperty("Published").Should().BeNull();
        typeof(AgentProfileOwnerDocument).GetProperty("Snapshot").Should().BeNull();
    }

    [Fact]
    public async Task ExecutionProjector_ShouldStayAbsentUntilFirstPublishThenExposeOnlySealedSnapshot()
    {
        var store = new RecordingDocumentStore<AgentProfileExecutionDocument>(x => x.Id);
        var projector = new AgentProfileExecutionCurrentStateProjector(
            store,
            new FixedProjectionClock(ObservedAt));

        await projector.ProjectAsync(
            ExecutionContext(),
            WrapCommitted(
                ProfileState(published: false),
                stateVersion: 41,
                eventId: "evt-profile-41"));

        (await store.GetAsync("profile-alpha")).Should().BeNull();

        var publishedState = ProfileState(published: true);
        await projector.ProjectAsync(
            ExecutionContext(),
            WrapCommitted(
                publishedState,
                stateVersion: 42,
                eventId: "evt-profile-42"));

        var written = await store.GetAsync("profile-alpha");
        written.Should().NotBeNull();
        written!.StateVersion.Should().Be(42);
        written.LastEventId.Should().Be("evt-profile-42");
        written.Snapshot.Should().NotBeSameAs(publishedState.Published);
        written.Snapshot.PublishedRevision.Should().Be(5);
        written.Snapshot.SkillBindings.Should().ContainSingle();
        written.Snapshot.SkillBindings[0].Skill.Package.Instructions.Should().Be("sealed skill body");
        typeof(AgentProfileExecutionDocument).GetProperty("Draft").Should().BeNull();
        typeof(AgentProfileExecutionDocument).GetProperty("LastMutation").Should().BeNull();
        typeof(AgentProfileExecutionDocument).GetProperty("PublishedSnapshotSha256").Should().BeNull();
    }

    [Fact]
    public async Task OwnerProjector_ShouldFailClosedForStaleAndConflictingWritesAndAcceptExactDuplicate()
    {
        var documentStore = new InMemoryProjectionDocumentStore<AgentProfileOwnerDocument, string>(
            static document => document.Id,
            static key => key);
        var dispatcher = new ProjectionStoreDispatcher<AgentProfileOwnerDocument>(
            [new ProjectionDocumentStoreBinding<AgentProfileOwnerDocument>(documentStore)]);
        var projector = new AgentProfileOwnerCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(ObservedAt));
        var context = OwnerContext();
        var state = ProfileState(published: true);
        var authoritative = WrapCommitted(state, stateVersion: 42, eventId: "evt-profile-42");

        await projector.ProjectAsync(context, authoritative);
        Func<Task> exactDuplicate = async () => await projector.ProjectAsync(context, authoritative);
        Func<Task> stale = async () => await projector.ProjectAsync(
            context,
            WrapCommitted(state, stateVersion: 41, eventId: "evt-profile-41"));
        Func<Task> conflict = async () => await projector.ProjectAsync(
            context,
            WrapCommitted(state, stateVersion: 42, eventId: "evt-profile-conflict"));

        await exactDuplicate.Should().NotThrowAsync();
        await stale.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Stale*");
        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Conflict*");
        var persisted = await documentStore.GetAsync("profile-alpha");
        persisted.Should().NotBeNull();
        persisted!.StateVersion.Should().Be(42);
        persisted.LastEventId.Should().Be("evt-profile-42");
    }

    [Fact]
    public async Task Projectors_ShouldNotifyBootstrapObserversOnlyAfterAcceptedUpserts()
    {
        var observer = new RecordingAgentProfileReadModelMaterializationObserver();
        var namespaceProjector = new AgentProfileNamespaceCurrentStateProjector(
            new RecordingDocumentStore<AgentProfileNamespaceCatalogDocument>(x => x.Id),
            new FixedProjectionClock(ObservedAt),
            [observer]);
        var ownerProjector = new AgentProfileOwnerCurrentStateProjector(
            new RecordingDocumentStore<AgentProfileOwnerDocument>(x => x.Id),
            new FixedProjectionClock(ObservedAt),
            [observer]);
        var executionProjector = new AgentProfileExecutionCurrentStateProjector(
            new RecordingDocumentStore<AgentProfileExecutionDocument>(x => x.Id),
            new FixedProjectionClock(ObservedAt),
            [observer]);

        await namespaceProjector.ProjectAsync(
            NamespaceContext(),
            WrapCommitted(new AgentProfileNamespaceState(), 41, "evt-namespace-41"));
        await ownerProjector.ProjectAsync(
            OwnerContext(),
            WrapCommitted(ProfileState(published: true), 42, "evt-owner-42"));
        await executionProjector.ProjectAsync(
            ExecutionContext(),
            WrapCommitted(ProfileState(published: true), 42, "evt-execution-42"));

        observer.NotificationCount.Should().Be(3);
    }

    [Fact]
    public async Task Projector_ShouldNotNotifyBootstrapObserverWhenUpsertIsRejected()
    {
        var observer = new RecordingAgentProfileReadModelMaterializationObserver();
        var projector = new AgentProfileNamespaceCurrentStateProjector(
            new RejectedProjectionWriteDispatcher<AgentProfileNamespaceCatalogDocument>(),
            new FixedProjectionClock(ObservedAt),
            [observer]);

        var act = async () => await projector.ProjectAsync(
            NamespaceContext(),
            WrapCommitted(new AgentProfileNamespaceState(), 41, "evt-namespace-conflict"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Conflict*");
        observer.NotificationCount.Should().Be(0);
    }

    private static AgentProfileNamespaceCurrentStateProjectionContext NamespaceContext() =>
        new()
        {
            RootActorId = AgentProfileActorIds.Namespace,
            ProjectionKind = "agent-profile-namespaces",
        };

    private sealed class RecordingAgentProfileReadModelMaterializationObserver
        : IAgentProfileReadModelMaterializationObserver
    {
        public int NotificationCount { get; private set; }

        public void OnAgentProfileReadModelMaterialized() => NotificationCount++;
    }

    private sealed class RejectedProjectionWriteDispatcher<TReadModel>
        : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public Task<ProjectionWriteResult> UpsertAsync(
            TReadModel readModel,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Conflict());

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Conflict());
    }

    private static AgentProfileOwnerCurrentStateProjectionContext OwnerContext() =>
        new()
        {
            RootActorId = "profile-actor-alpha",
            ProjectionKind = "agent-profile-management",
        };

    private static AgentProfileExecutionCurrentStateProjectionContext ExecutionContext() =>
        new()
        {
            RootActorId = "profile-actor-alpha",
            ProjectionKind = "agent-profile-execution",
        };

    private static AgentProfileNamespaceEntryState NamespaceEntry(
        string profileId,
        string profileSlug,
        AgentProfileProvisioningStatus status,
        bool published)
    {
        var entry = new AgentProfileNamespaceEntryState
        {
            Identity = Identity(profileId, profileSlug),
            ProfileActorId = $"actor-{profileId}",
            Status = status,
            InitialContent = new AgentProfileContent
            {
                DisplayName = $"Draft {profileSlug}",
                Instructions = "draft-secret",
            },
        };
        if (status == AgentProfileProvisioningStatus.Failed)
        {
            entry.Failure = new AgentProfileSafeDiagnostic
            {
                Code = "PROVISIONING_FAILED",
                Message = "internal failure",
            };
        }

        if (published)
        {
            entry.PublishedSummary = new AgentProfilePublishedSummary
            {
                Reference = Reference(profileSlug),
                DisplayName = $"Published {profileSlug}",
                Purpose = $"Safe summary {profileSlug}",
                PublishedRevision = 5,
                SnapshotSha256 = ByteString.CopyFromUtf8($"summary-{profileSlug}"),
            };
        }

        return entry;
    }

    private static AgentProfileState ProfileState(bool published)
    {
        var identity = Identity("profile-alpha", "alpha");
        var state = new AgentProfileState
        {
            Identity = identity,
            NamespaceActorId = AgentProfileActorIds.Namespace,
            Draft = new AgentProfileContent
            {
                DisplayName = "Draft alpha",
                Purpose = "Draft purpose",
                Instructions = "draft instructions",
                ToolPolicy = new AgentProfileToolPolicy
                {
                    Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
                    ToolNames = { "calendar.read" },
                },
                SkillBindings =
                {
                    new AgentProfileSkillBinding
                    {
                        BindingId = "binding-alpha",
                        ActivationMode = AgentProfileSkillActivationMode.Always,
                        Skill = ExactSkillReference(),
                    },
                },
            },
            DraftRevision = 6,
            DraftSha256 = ByteString.CopyFromUtf8("draft-sha-6"),
            PublishedRevision = published ? 5 : 0,
            LastMutation = new AgentProfileMutationOutcome
            {
                Operation = new AgentProfileOperationFact
                {
                    OperationId = "operation-42",
                    CommandId = "command-42",
                    CorrelationId = "correlation-42",
                },
                Status = AgentProfileMutationStatus.Applied,
                DraftRevision = 6,
                DraftSha256 = ByteString.CopyFromUtf8("draft-sha-6"),
                PublishedRevision = published ? 5 : 0,
                PublishedSnapshotSha256 = published
                    ? ByteString.CopyFromUtf8("snapshot-sha-5")
                    : ByteString.Empty,
            },
        };

        if (published)
            state.Published = PublishedSnapshot(identity);

        return state;
    }

    private static AgentProfilePublishedSnapshot PublishedSnapshot(AgentProfileIdentity identity) =>
        new()
        {
            Identity = identity.Clone(),
            DisplayName = "Published alpha",
            Purpose = "Published purpose",
            Instructions = "published instructions",
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
                ToolNames = { "calendar.read" },
            },
            SkillBindings =
            {
                new SealedAgentProfileSkillBinding
                {
                    BindingId = "binding-alpha",
                    ActivationMode = AgentProfileSkillActivationMode.Always,
                    Skill = new SealedAgentProfileSkill
                    {
                        ExactReference = ExactSkillReference(),
                        Package = new ResolvedOrnnSkillPackage
                        {
                            SkillGuid = "11111111-1111-1111-1111-111111111111",
                            LiteralVersion = "1.2",
                            CanonicalName = "calendar",
                            PublisherId = "publisher-alpha",
                            UpstreamSkillHash = "upstream-hash",
                            Instructions = "sealed skill body",
                        },
                        ContentSha256 = ByteString.CopyFromUtf8("sealed-content-sha"),
                    },
                },
            },
            PublishedRevision = 5,
            SourceDraftSha256 = ByteString.CopyFromUtf8("source-draft-sha-5"),
            SnapshotSha256 = ByteString.CopyFromUtf8("snapshot-sha-5"),
        };

    private static AgentProfileIdentity Identity(string profileId, string profileSlug) =>
        new()
        {
            ProfileId = profileId,
            Owner = Owner(),
            OwningScopeId = "scope-owner",
            Reference = Reference(profileSlug),
        };

    private static AgentProfileOwnerIdentity Owner() =>
        new()
        {
            User = new AgentProfileUserOwnerIdentity
            {
                IdentityProvider = "nyxid",
                SubjectId = "owner-subject",
            },
        };

    private static AgentProfileReference Reference(string profileSlug) =>
        new()
        {
            OwnerHandle = "owner-alpha",
            ProfileSlug = profileSlug,
        };

    private static ExactOrnnSkillReference ExactSkillReference() =>
        new()
        {
            SkillGuid = "11111111-1111-1111-1111-111111111111",
            LiteralVersion = "1.2",
            ExpectedName = "calendar",
            ExpectedPublisherId = "publisher-alpha",
        };

    private static EventEnvelope WrapCommitted(
        IMessage stateRoot,
        long stateVersion,
        string eventId) =>
        new()
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(ObservedAt),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "authoritative-actor",
                    EventId = eventId,
                    Version = stateVersion,
                    Timestamp = Timestamp.FromDateTimeOffset(ObservedAt),
                    EventData = Any.Pack(new StringValue { Value = "committed" }),
                },
                StateRoot = Any.Pack(stateRoot),
            }),
        };
}
