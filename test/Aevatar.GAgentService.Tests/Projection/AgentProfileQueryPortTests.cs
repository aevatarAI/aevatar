using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Projection.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class AgentProfileQueryPortTests
{
    private static readonly DateTimeOffset UpdatedAt =
        DateTimeOffset.Parse("2026-07-23T02:03:04+00:00");

    [Fact]
    public async Task NamespaceQuery_ShouldResolveOnlyActiveMappingsAndSafeSummaryFacts()
    {
        var store = new RecordingDocumentStore<AgentProfileNamespaceCatalogDocument>(x => x.Id);
        var document = NamespaceDocument();
        document.Entries.Add(CatalogEntry(
            "profile-failed",
            "failed-profile",
            AgentProfileProvisioningStatus.Failed));
        await store.UpsertAsync(document);
        IAgentProfileNamespaceQueryPort query = new ProjectionAgentProfileNamespaceQueryPort(store);

        var byReference = await query.GetByReferenceAsync(Reference("alpha"));
        var owned = await query.GetOwnedAsync(Owner(), "scope-owner", "alpha");
        var failed = await query.GetByReferenceAsync(Reference("failed-profile"));
        var wrongScope = await query.GetOwnedAsync(Owner(), "scope-other", "alpha");

        byReference.Should().NotBeNull();
        owned.Should().NotBeNull();
        failed.Should().BeNull();
        wrongScope.Should().BeNull();
        byReference!.AuthorityStateVersion.Should().Be(12);
        byReference.LastEventId.Should().Be("evt-namespace-12");
        byReference.ProfileId.Should().Be("profile-alpha");
        byReference.Reference.Should().BeEquivalentTo(Reference("alpha"));
        byReference.Owner.Should().BeEquivalentTo(Owner());
        byReference.OwningScopeId.Should().Be("scope-owner");
        byReference.Status.Should().Be(AgentProfileProvisioningStatus.Active);
        byReference.PublishedSummary.Should().NotBeNull();
        byReference.PublishedSummary!.DisplayName.Should().Be("Published alpha");
        byReference.PublishedSummary.Purpose.Should().Be("Safe purpose");
        owned.Should().BeEquivalentTo(byReference);
        store.LastQueryTake.Should().Be(0);
    }

    [Fact]
    public async Task QueryPorts_ShouldReturnDeepClonesWithoutMutatingStoredDocuments()
    {
        var namespaceStore = new RecordingDocumentStore<AgentProfileNamespaceCatalogDocument>(x => x.Id);
        var ownerStore = new RecordingDocumentStore<AgentProfileOwnerDocument>(x => x.Id);
        var executionStore = new RecordingDocumentStore<AgentProfileExecutionDocument>(x => x.Id);
        var namespaceDocument = NamespaceDocument();
        var ownerDocument = OwnerDocument();
        var executionDocument = ExecutionDocument();
        await namespaceStore.UpsertAsync(namespaceDocument);
        await ownerStore.UpsertAsync(ownerDocument);
        await executionStore.UpsertAsync(executionDocument);
        IAgentProfileNamespaceQueryPort namespaceQuery =
            new ProjectionAgentProfileNamespaceQueryPort(namespaceStore);
        IAgentProfileManagementQueryPort managementQuery =
            new ProjectionAgentProfileManagementQueryPort(ownerStore);
        IAgentProfileExecutionSnapshotQueryPort executionQuery =
            new ProjectionAgentProfileExecutionSnapshotQueryPort(executionStore);

        var firstNamespace = await namespaceQuery.GetByReferenceAsync(Reference("alpha"));
        var firstManagement = await managementQuery.GetAsync("profile-alpha");
        var firstExecution = await executionQuery.GetAsync("profile-alpha");
        firstNamespace.Should().NotBeNull();
        firstManagement.Should().NotBeNull();
        firstExecution.Should().NotBeNull();

        var returnedReference = firstNamespace!.Reference;
        returnedReference.ProfileSlug = "mutated";
        var returnedOwner = firstNamespace.Owner;
        returnedOwner.User.SubjectId = "mutated";
        var returnedSummary = firstNamespace.PublishedSummary!;
        returnedSummary.DisplayName = "mutated";
        var returnedIdentity = firstManagement!.Identity;
        returnedIdentity.Reference.ProfileSlug = "mutated";
        var returnedDraft = firstManagement.Draft;
        returnedDraft.DisplayName = "mutated";
        returnedDraft.SkillBindings[0].Skill.ExpectedName = "mutated";
        var returnedMutation = firstManagement.LastMutation!;
        returnedMutation.Operation.OperationId = "mutated";
        var returnedSnapshot = firstExecution!.Snapshot;
        returnedSnapshot.Instructions = "mutated";
        returnedSnapshot.SkillBindings[0].Skill.Package.Instructions = "mutated";

        var secondNamespace = await namespaceQuery.GetByReferenceAsync(Reference("alpha"));
        var secondManagement = await managementQuery.GetAsync("profile-alpha");
        var secondExecution = await executionQuery.GetAsync("profile-alpha");

        secondNamespace.Should().NotBeSameAs(firstNamespace);
        secondNamespace!.Reference.ProfileSlug.Should().Be("alpha");
        secondNamespace.Owner.User.SubjectId.Should().Be("owner-subject");
        secondNamespace.PublishedSummary!.DisplayName.Should().Be("Published alpha");
        secondManagement.Should().NotBeSameAs(firstManagement);
        secondManagement!.Identity.Reference.ProfileSlug.Should().Be("alpha");
        secondManagement.Draft.DisplayName.Should().Be("Draft alpha");
        secondManagement.Draft.SkillBindings[0].Skill.ExpectedName.Should().Be("calendar");
        secondManagement.LastMutation!.Operation.OperationId.Should().Be("operation-14");
        secondExecution.Should().NotBeSameAs(firstExecution);
        secondExecution!.Snapshot.Instructions.Should().Be("published instructions");
        secondExecution.Snapshot.SkillBindings[0].Skill.Package.Instructions.Should().Be("sealed skill body");
        namespaceDocument.Entries[0].Reference.ProfileSlug.Should().Be("alpha");
        ownerDocument.Draft.DisplayName.Should().Be("Draft alpha");
        executionDocument.Snapshot.Instructions.Should().Be("published instructions");
        namespaceStore.LastQueryTake.Should().Be(0);
        ownerStore.LastQueryTake.Should().Be(0);
        executionStore.LastQueryTake.Should().Be(0);
    }

    [Fact]
    public async Task QueryPorts_ShouldRejectEmptyExternalIdentitiesWithoutListingOrFallback()
    {
        var namespaceStore = new RecordingDocumentStore<AgentProfileNamespaceCatalogDocument>(x => x.Id);
        var ownerStore = new RecordingDocumentStore<AgentProfileOwnerDocument>(x => x.Id);
        var executionStore = new RecordingDocumentStore<AgentProfileExecutionDocument>(x => x.Id);
        IAgentProfileNamespaceQueryPort namespaceQuery =
            new ProjectionAgentProfileNamespaceQueryPort(namespaceStore);
        IAgentProfileManagementQueryPort managementQuery =
            new ProjectionAgentProfileManagementQueryPort(ownerStore);
        IAgentProfileExecutionSnapshotQueryPort executionQuery =
            new ProjectionAgentProfileExecutionSnapshotQueryPort(executionStore);

        (await namespaceQuery.GetByReferenceAsync(new AgentProfileReference())).Should().BeNull();
        (await namespaceQuery.GetOwnedAsync(new AgentProfileOwnerIdentity(), "", "")).Should().BeNull();
        (await managementQuery.GetAsync(" ")).Should().BeNull();
        (await executionQuery.GetAsync(" ")).Should().BeNull();
        namespaceStore.LastQueryTake.Should().Be(0);
        ownerStore.LastQueryTake.Should().Be(0);
        executionStore.LastQueryTake.Should().Be(0);
    }

    private static AgentProfileNamespaceCatalogDocument NamespaceDocument() =>
        new()
        {
            Id = AgentProfileActorIds.Namespace,
            ActorId = AgentProfileActorIds.Namespace,
            StateVersion = 12,
            LastEventId = "evt-namespace-12",
            UpdatedAt = UpdatedAt,
            Entries = { CatalogEntry("profile-alpha", "alpha", AgentProfileProvisioningStatus.Active) },
        };

    private static AgentProfileCatalogEntryDocument CatalogEntry(
        string profileId,
        string profileSlug,
        AgentProfileProvisioningStatus status) =>
        new()
        {
            ProfileId = profileId,
            Reference = Reference(profileSlug),
            Owner = Owner(),
            OwningScopeId = "scope-owner",
            Status = status,
            PublishedSummary = new AgentProfilePublishedSummary
            {
                Reference = Reference(profileSlug),
                DisplayName = $"Published {profileSlug}",
                Purpose = "Safe purpose",
                PublishedRevision = 5,
                SnapshotSha256 = ByteString.CopyFromUtf8("summary-sha-5"),
            },
        };

    private static AgentProfileOwnerDocument OwnerDocument() =>
        new()
        {
            Id = "profile-alpha",
            ActorId = "profile-actor-alpha",
            StateVersion = 14,
            LastEventId = "evt-profile-14",
            UpdatedAt = UpdatedAt,
            Identity = Identity(),
            Draft = Draft(),
            DraftRevision = 6,
            DraftSha256 = ByteString.CopyFromUtf8("draft-sha-6"),
            PublishedRevision = 5,
            PublishedSnapshotSha256 = ByteString.CopyFromUtf8("snapshot-sha-5"),
            PublishedSourceDraftSha256 = ByteString.CopyFromUtf8("source-draft-sha-5"),
            LastMutation = new AgentProfileMutationOutcome
            {
                Operation = new AgentProfileOperationFact
                {
                    OperationId = "operation-14",
                    CommandId = "command-14",
                },
                Status = AgentProfileMutationStatus.Applied,
                DraftRevision = 6,
                PublishedRevision = 5,
            },
        };

    private static AgentProfileExecutionDocument ExecutionDocument() =>
        new()
        {
            Id = "profile-alpha",
            ActorId = "profile-actor-alpha",
            StateVersion = 14,
            LastEventId = "evt-profile-14",
            UpdatedAt = UpdatedAt,
            Snapshot = PublishedSnapshot(),
        };

    private static AgentProfileContent Draft() =>
        new()
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
        };

    private static AgentProfilePublishedSnapshot PublishedSnapshot() =>
        new()
        {
            Identity = Identity(),
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
                    },
                },
            },
            PublishedRevision = 5,
            SourceDraftSha256 = ByteString.CopyFromUtf8("source-draft-sha-5"),
            SnapshotSha256 = ByteString.CopyFromUtf8("snapshot-sha-5"),
        };

    private static AgentProfileIdentity Identity() =>
        new()
        {
            ProfileId = "profile-alpha",
            Owner = Owner(),
            OwningScopeId = "scope-owner",
            Reference = Reference("alpha"),
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
}
