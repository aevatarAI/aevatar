using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class AgentProfileApplicationServiceTests
{
    private static readonly AgentProfileOwner ScopeOwner = AgentProfileOwners.ForScope("scope-alpha");
    private static readonly AgentProfileOwner SystemOwner = AgentProfileOwners.ForSystem();
    private static readonly DateTimeOffset PublishedAt =
        DateTimeOffset.Parse("2026-07-30T04:00:00Z");

    [Fact]
    public async Task ListAsync_ShouldPageCatalogOutcomesWithoutHostSideIdentityGuessing()
    {
        var catalog = new FakeCatalogQuery
        {
            Snapshot = Catalog(
                ScopeOwner,
                12,
                Entry("prof-a", "alpha", AgentProfileProvisioningStatus.Active),
                Entry("prof-b", "beta", AgentProfileProvisioningStatus.Failed),
                Entry("prof-c", "charlie", AgentProfileProvisioningStatus.Active)),
        };
        var service = CreateService(catalog: catalog);

        var first = await service.ListAsync(ScopeOwner, cursor: null, pageSize: 2);
        var second = await service.ListAsync(ScopeOwner, first.NextCursor, pageSize: 2);

        first.Items.Select(x => x.ProfileId).Should().Equal("prof-a", "prof-b");
        first.Items.Should().Contain(x => x.ProfileId == "prof-b" && x.Status == AgentProfileProvisioningStatus.Failed);
        first.NextCursor.Should().NotBeNullOrWhiteSpace();
        second.Items.Should().ContainSingle(x => x.ProfileId == "prof-c" && x.ProfileSlug == "charlie");
        second.NextCursor.Should().BeNull();
        catalog.Owner.Should().BeEquivalentTo(ScopeOwner);
    }

    [Fact]
    public async Task GetAsync_ShouldResolveTypedIdentityFromOwnerCatalogBeforeManagementQuery()
    {
        var catalog = new FakeCatalogQuery
        {
            Snapshot = Catalog(ScopeOwner, 7, Entry("prof-alpha", "research", AgentProfileProvisioningStatus.Active)),
        };
        var management = new FakeManagementQuery
        {
            Snapshot = Management(ScopeOwner, "prof-alpha", "research", authorityVersion: 9),
        };
        var service = CreateService(catalog: catalog, management: management);

        var detail = await service.GetAsync(ScopeOwner, "research");

        detail.Should().NotBeNull();
        detail!.Identity.ProfileId.Should().Be("prof-alpha");
        detail.Identity.Owner.Should().BeEquivalentTo(ScopeOwner);
        detail.StrongETag.Should().Be("\"agent-profile-v9\"");
        management.Identity.Should().BeEquivalentTo(new AgentProfileIdentity
        {
            ProfileId = "prof-alpha",
            ProfileSlug = "research",
            Owner = ScopeOwner,
        });
    }

    [Fact]
    public async Task GetAsync_ShouldReportProtectedExecutionAvailabilityForPublishedRevision()
    {
        var entry = PublishedEntry("prof-alpha", "research", revision: 3);
        var catalog = new FakeCatalogQuery
        {
            Snapshot = Catalog(ScopeOwner, 7, entry),
        };
        var managementSnapshot = Management(ScopeOwner, entry.ProfileId, entry.ProfileSlug, authorityVersion: 9) with
        {
            PublishedRevision = entry.PublishedRevision,
            PublishedSnapshotSha256 = entry.SnapshotSha256,
        };
        var management = new FakeManagementQuery { Snapshot = managementSnapshot };
        var execution = new FakeExecutionQuery();
        var service = CreateService(catalog, management, execution);

        var unavailable = await service.GetAsync(ScopeOwner, entry.ProfileSlug);

        unavailable.Should().NotBeNull();
        unavailable!.ExecutionAvailable.Should().BeFalse();
        execution.Target.Should().BeEquivalentTo(new AgentProfileBindingTarget
        {
            Owner = ScopeOwner,
            ProfileId = entry.ProfileId,
            PublishedRevision = entry.PublishedRevision,
            SnapshotSha256 = entry.SnapshotSha256,
        });

        execution.Snapshot = new AgentProfileExecutionSnapshot(
            "profile-actor",
            11,
            managementSnapshot.Identity.Clone(),
            new AgentProfilePublishedSnapshot
            {
                Identity = managementSnapshot.Identity.Clone(),
                PublishedRevision = entry.PublishedRevision,
                SnapshotSha256 = entry.SnapshotSha256,
            },
            DateTimeOffset.UtcNow);

        var available = await service.GetAsync(ScopeOwner, entry.ProfileSlug);

        available.Should().NotBeNull();
        available!.ExecutionAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_ShouldDeriveStableOpaqueIdentityAndStampTypedOwner()
    {
        var actorPort = new RecordingActorPort();
        var service = CreateService(actorPort: actorPort);
        var request = new AgentProfileCreateRequest(
            ScopeOwner,
            "research-assistant",
            "idem-create-alpha",
            "nyx-user-alpha");

        var first = await service.CreateAsync(request);
        var replay = await service.CreateAsync(request);

        first.ProfileId.Should().StartWith("prof_");
        replay.ProfileId.Should().Be(first.ProfileId);
        replay.OperationId.Should().Be(first.OperationId);
        first.Accepted.Should().BeTrue();
        actorPort.CreateCommands.Should().HaveCount(2);
        actorPort.CreateCommands.Should().OnlyContain(command =>
            command.Owner.Equals(ScopeOwner) &&
            command.ProfileId == first.ProfileId &&
            command.ProfileSlug == "research-assistant" &&
            command.Operation.AuditSubject == "nyx-user-alpha" &&
            command.Operation.InputSha256.Length == 32);
    }

    [Fact]
    public async Task UpdateDraftAsync_ShouldUseCatalogIdentityAndExpectedAuthorityVersion()
    {
        var catalog = new FakeCatalogQuery
        {
            Snapshot = Catalog(ScopeOwner, 7, Entry("prof-alpha", "research", AgentProfileProvisioningStatus.Active)),
        };
        var management = new FakeManagementQuery
        {
            Snapshot = Management(ScopeOwner, "prof-alpha", "research", authorityVersion: 9),
        };
        var actorPort = new RecordingActorPort();
        var service = CreateService(catalog, management, actorPort: actorPort);
        var draft = Draft("Research v2");

        var receipt = await service.UpdateDraftAsync(new AgentProfileDraftUpdateRequest(
            ScopeOwner,
            "research",
            draft,
            ExpectedAuthorityStateVersion: 9,
            IdempotencyKey: "idem-draft-alpha",
            AuditSubject: "nyx-user-alpha"));

        receipt.Accepted.Should().BeTrue();
        actorPort.DraftCommands.Should().ContainSingle();
        var command = actorPort.DraftCommands[0];
        command.Identity.ProfileId.Should().Be("prof-alpha");
        command.Identity.Owner.Should().BeEquivalentTo(ScopeOwner);
        command.ExpectedAuthorityStateVersion.Should().Be(9);
        command.Draft.Should().BeEquivalentTo(AgentProfileDeterminism.NormalizeDraft(draft));
    }

    [Fact]
    public async Task SetBindingAsync_ShouldFailClosedWhenProtectedExecutionSnapshotIsUnavailable()
    {
        var scopeCatalog = Catalog(ScopeOwner, 4);
        var systemCatalog = Catalog(
            SystemOwner,
            8,
            PublishedEntry("prof-system", "system-research", revision: 3));
        var catalog = new FakeCatalogQuery
        {
            Snapshots =
            {
                [OwnerKey(ScopeOwner)] = scopeCatalog,
                [OwnerKey(SystemOwner)] = systemCatalog,
            },
        };
        var execution = new FakeExecutionQuery { Snapshot = null };
        var actorPort = new RecordingActorPort();
        var service = CreateService(catalog: catalog, execution: execution, actorPort: actorPort);

        var act = () => service.SetBindingAsync(new AgentProfileBindingUpdateRequest(
            ScopeOwner,
            AgentProfilePolicies.NyxIdChatAgentKind,
            new AgentProfileReference
            {
                OwnerKind = AgentProfileReferenceOwnerKind.System,
                ProfileSlug = "system-research",
            },
            ExpectedAuthorityStateVersion: 4,
            IdempotencyKey: "idem-binding-alpha",
            AuditSubject: "nyx-user-alpha"));

        await act.Should().ThrowAsync<AgentProfileUnavailableException>()
            .WithMessage("*protected execution*");
        actorPort.BindingCommands.Should().BeEmpty();
        execution.Target.Should().NotBeNull();
        execution.Target!.Owner.Should().BeEquivalentTo(SystemOwner);
        execution.Target.ProfileId.Should().Be("prof-system");
        execution.Target.PublishedRevision.Should().Be(3);
    }

    [Fact]
    public async Task SetBindingAsync_ShouldDispatchScopeAdmissionAfterProtectedSnapshotMatches()
    {
        var targetEntry = PublishedEntry("prof-system", "system-research", revision: 3);
        var catalog = new FakeCatalogQuery
        {
            Snapshots =
            {
                [OwnerKey(ScopeOwner)] = Catalog(ScopeOwner, 4),
                [OwnerKey(SystemOwner)] = Catalog(SystemOwner, 8, targetEntry),
            },
        };
        var target = new AgentProfileBindingTarget
        {
            Owner = SystemOwner,
            ProfileId = targetEntry.ProfileId,
            PublishedRevision = targetEntry.PublishedRevision,
            SnapshotSha256 = targetEntry.SnapshotSha256,
        };
        var execution = new FakeExecutionQuery
        {
            Snapshot = new AgentProfileExecutionSnapshot(
                "profile-actor",
                11,
                new AgentProfileIdentity
                {
                    Owner = SystemOwner,
                    ProfileId = targetEntry.ProfileId,
                    ProfileSlug = targetEntry.ProfileSlug,
                },
                new AgentProfilePublishedSnapshot
                {
                    Identity = new AgentProfileIdentity
                    {
                        Owner = SystemOwner,
                        ProfileId = targetEntry.ProfileId,
                        ProfileSlug = targetEntry.ProfileSlug,
                    },
                    PublishedRevision = targetEntry.PublishedRevision,
                    SnapshotSha256 = targetEntry.SnapshotSha256,
                },
                DateTimeOffset.UtcNow),
        };
        var actorPort = new RecordingActorPort();
        var service = CreateService(catalog: catalog, execution: execution, actorPort: actorPort);

        await service.SetBindingAsync(new AgentProfileBindingUpdateRequest(
            ScopeOwner,
            AgentProfilePolicies.NyxIdChatAgentKind,
            new AgentProfileReference
            {
                OwnerKind = AgentProfileReferenceOwnerKind.System,
                ProfileSlug = "system-research",
            },
            ExpectedAuthorityStateVersion: 4,
            IdempotencyKey: "idem-binding-alpha",
            AuditSubject: "nyx-user-alpha"));

        actorPort.BindingCommands.Should().ContainSingle();
        var command = actorPort.BindingCommands[0];
        command.Owner.Should().BeEquivalentTo(ScopeOwner);
        command.Target.Should().BeEquivalentTo(target);
        command.AdmissionCase.Should().Be(SetAgentProfileDefaultBindingCommand.AdmissionOneofCase.Scope);
        command.ExpectedAuthorityStateVersion.Should().Be(4);
    }

    [Fact]
    public async Task PublishAsync_ShouldSealCurrentManagementDraftAndDispatchCandidate()
    {
        var entry = Entry("prof-alpha", "research", AgentProfileProvisioningStatus.Active);
        entry.ProfileActorId = "profile-actor";
        var catalog = new FakeCatalogQuery
        {
            Snapshot = Catalog(ScopeOwner, 8, entry),
        };
        var managementSnapshot = Management(ScopeOwner, entry.ProfileId, entry.ProfileSlug, authorityVersion: 8);
        var management = new FakeManagementQuery { Snapshot = managementSnapshot };
        var sealer = new RecordingSealer();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            catalog: catalog,
            management: management,
            actorPort: actorPort,
            sealer: sealer,
            timeProvider: new FixedTimeProvider(PublishedAt));

        var receipt = await service.PublishAsync(new AgentProfilePublishRequest(
            ScopeOwner,
            "research",
            ExpectedAuthorityStateVersion: 8,
            IdempotencyKey: "publish-alpha",
            AuditSubject: "nyx-user-alpha",
            NyxIdAccessToken: "turn-token"));

        receipt.Accepted.Should().BeTrue();
        sealer.Identity.Should().BeEquivalentTo(managementSnapshot.Identity);
        sealer.Draft.Should().BeEquivalentTo(managementSnapshot.Draft);
        sealer.Context.Should().Be(new AgentProfileSealingContext(
            CurrentDraftRevision: 2,
            NextPublishedRevision: 2,
            PublishedAt,
            NyxIdAccessToken: "turn-token"));
        actorPort.PublishCommands.Should().ContainSingle();
        var command = actorPort.PublishCommands[0];
        command.ExpectedAuthorityStateVersion.Should().Be(8);
        command.SourceDraftSha256.Should().Equal(managementSnapshot.DraftSha256);
        command.Snapshot.PublishedRevision.Should().Be(2);
    }

    [Fact]
    public async Task ValidateAsync_ShouldValidateCurrentDraftWithoutDispatchingMutation()
    {
        var entry = Entry("prof-alpha", "research", AgentProfileProvisioningStatus.Active);
        entry.ProfileActorId = "profile-actor";
        var managementSnapshot = Management(ScopeOwner, entry.ProfileId, entry.ProfileSlug, authorityVersion: 8);
        var sealer = new RecordingSealer();
        var actorPort = new RecordingActorPort();
        var service = CreateService(
            catalog: new FakeCatalogQuery { Snapshot = Catalog(ScopeOwner, 8, entry) },
            management: new FakeManagementQuery { Snapshot = managementSnapshot },
            actorPort: actorPort,
            sealer: sealer,
            timeProvider: new FixedTimeProvider(PublishedAt));

        var result = await service.ValidateAsync(
            ScopeOwner,
            "research",
            "turn-token");

        result.IsValid.Should().BeTrue();
        result.DraftRevision.Should().Be(2);
        result.DraftSha256.Should().Equal(managementSnapshot.DraftSha256);
        result.Diagnostics.Should().BeEmpty();
        sealer.Context.Should().Be(new AgentProfileSealingContext(
            CurrentDraftRevision: 2,
            NextPublishedRevision: 2,
            PublishedAt,
            NyxIdAccessToken: "turn-token"));
        actorPort.CreateCommands.Should().BeEmpty();
        actorPort.DraftCommands.Should().BeEmpty();
        actorPort.PublishCommands.Should().BeEmpty();
        actorPort.BindingCommands.Should().BeEmpty();
        actorPort.ClearBindingCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBindingAsync_ShouldReturnNamespaceBindingWithStrongResourceETag()
    {
        var target = new AgentProfileBindingTarget
        {
            Owner = SystemOwner.Clone(),
            ProfileId = "prof-system",
            PublishedRevision = 3,
            SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)9, 32).ToArray()),
        };
        var binding = new AgentProfileDefaultBinding
        {
            AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
            Target = target,
            Scope = new AgentProfileScopeBindingAdmission(),
        };
        var catalog = new AgentProfileCatalogSnapshot(
            "namespace-actor",
            14,
            ScopeOwner.Clone(),
            [],
            [binding],
            null,
            DateTimeOffset.UtcNow);
        var service = CreateService(catalog: new FakeCatalogQuery { Snapshot = catalog });

        var result = await service.GetBindingAsync(ScopeOwner, AgentProfilePolicies.NyxIdChatAgentKind);

        result.Should().NotBeNull();
        result!.Binding.Should().BeEquivalentTo(binding);
        result.AuthorityStateVersion.Should().Be(14);
        result.StrongETag.Should().Be("\"agent-profile-binding-v14\"");
    }

    [Fact]
    public async Task GetBindingAsync_ShouldReturnVersionedEmptyResourceForFirstBinding()
    {
        var service = CreateService(catalog: new FakeCatalogQuery());

        var result = await service.GetBindingAsync(
            ScopeOwner,
            AgentProfilePolicies.NyxIdChatAgentKind);

        result.Should().NotBeNull();
        result.Binding.Should().BeNull();
        result.AuthorityStateVersion.Should().Be(0);
        result.StrongETag.Should().Be("\"agent-profile-binding-v0\"");
        result.UpdatedAt.Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task ClearBindingAsync_ShouldDispatchExpectedNamespaceVersion()
    {
        var actorPort = new RecordingActorPort();
        var service = CreateService(actorPort: actorPort);

        var receipt = await service.ClearBindingAsync(new AgentProfileBindingClearRequest(
            ScopeOwner,
            AgentProfilePolicies.NyxIdChatAgentKind,
            ExpectedAuthorityStateVersion: 14,
            IdempotencyKey: "clear-binding-alpha",
            AuditSubject: "nyx-user-alpha"));

        receipt.Accepted.Should().BeTrue();
        actorPort.ClearBindingCommands.Should().ContainSingle();
        var command = actorPort.ClearBindingCommands[0];
        command.Owner.Should().BeEquivalentTo(ScopeOwner);
        command.AgentKind.Should().Be(AgentProfilePolicies.NyxIdChatAgentKind);
        command.ExpectedAuthorityStateVersion.Should().Be(14);
        command.Operation.AuditSubject.Should().Be("nyx-user-alpha");
    }

    private static AgentProfileApplicationService CreateService(
        FakeCatalogQuery? catalog = null,
        FakeManagementQuery? management = null,
        FakeExecutionQuery? execution = null,
        RecordingActorPort? actorPort = null,
        IAgentProfileSkillSealer? sealer = null,
        TimeProvider? timeProvider = null) =>
        new(
            catalog ?? new FakeCatalogQuery(),
            management ?? new FakeManagementQuery(),
            execution ?? new FakeExecutionQuery(),
            actorPort ?? new RecordingActorPort(),
            sealer ?? new RecordingSealer(),
            timeProvider ?? TimeProvider.System);

    private static AgentProfileCatalogSnapshot Catalog(
        AgentProfileOwner owner,
        long authorityVersion,
        params AgentProfileCatalogEntry[] entries) =>
        new(
            "namespace-actor",
            authorityVersion,
            owner.Clone(),
            entries,
            [],
            null,
            DateTimeOffset.UtcNow);

    private static AgentProfileManagementSnapshot Management(
        AgentProfileOwner owner,
        string profileId,
        string slug,
        long authorityVersion)
    {
        var draft = Draft("Research");
        return new AgentProfileManagementSnapshot(
            "profile-actor",
            authorityVersion,
            new AgentProfileIdentity
            {
                Owner = owner.Clone(),
                ProfileId = profileId,
                ProfileSlug = slug,
            },
            draft,
            2,
            AgentProfileDeterminism.ComputeDraftDigest(draft),
            "Research",
            "Purpose",
            1,
            ByteString.CopyFrom(Enumerable.Repeat((byte)7, 32).ToArray()),
            DateTimeOffset.UtcNow,
            null,
            DateTimeOffset.UtcNow);
    }

    private static AgentProfileCatalogEntry Entry(
        string profileId,
        string slug,
        AgentProfileProvisioningStatus status) =>
        new()
        {
            ProfileId = profileId,
            ProfileSlug = slug,
            ProfileActorId = $"actor-{profileId}",
            Status = status,
        };

    private static AgentProfileCatalogEntry PublishedEntry(string profileId, string slug, long revision)
    {
        var entry = Entry(profileId, slug, AgentProfileProvisioningStatus.Active);
        entry.PublishedRevision = revision;
        entry.SnapshotSha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)9, 32).ToArray());
        return entry;
    }

    private static AgentProfileDraft Draft(string displayName) =>
        new()
        {
            DisplayName = displayName,
            Purpose = "Purpose",
            Instructions = "Use exact evidence.",
            RuntimeProfile = new Aevatar.AI.Abstractions.AgentProfileSnapshot
            {
                AgentKind = AgentProfilePolicies.NyxIdChatAgentKind,
                RouteToolSetRef = AgentProfilePolicies.NyxIdChatRouteToolSet,
            },
        };

    private static string OwnerKey(AgentProfileOwner owner) => owner.ToByteString().ToBase64();

    private sealed class FakeCatalogQuery : IAgentProfileCatalogQueryPort
    {
        public AgentProfileCatalogSnapshot? Snapshot { get; set; }
        public Dictionary<string, AgentProfileCatalogSnapshot> Snapshots { get; } = [];
        public AgentProfileOwner? Owner { get; private set; }

        public Task<AgentProfileCatalogSnapshot?> GetAsync(
            AgentProfileOwner owner,
            CancellationToken ct = default)
        {
            Owner = owner.Clone();
            return Task.FromResult(
                Snapshots.TryGetValue(OwnerKey(owner), out var snapshot) ? snapshot : Snapshot);
        }
    }

    private sealed class FakeManagementQuery : IAgentProfileManagementQueryPort
    {
        public AgentProfileManagementSnapshot? Snapshot { get; set; }
        public AgentProfileIdentity? Identity { get; private set; }

        public Task<AgentProfileManagementSnapshot?> GetAsync(
            AgentProfileIdentity identity,
            CancellationToken ct = default)
        {
            Identity = identity.Clone();
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class FakeExecutionQuery : IAgentProfileExecutionQueryPort
    {
        public AgentProfileExecutionSnapshot? Snapshot { get; set; }
        public AgentProfileBindingTarget? Target { get; private set; }

        public Task<AgentProfileExecutionSnapshot?> GetAsync(
            AgentProfileBindingTarget target,
            CancellationToken ct = default)
        {
            Target = target.Clone();
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class RecordingActorPort : IAgentProfileActorPort
    {
        public List<CreateAgentProfileCommand> CreateCommands { get; } = [];
        public List<UpdateAgentProfileDraftCommand> DraftCommands { get; } = [];
        public List<PublishAgentProfileCommand> PublishCommands { get; } = [];
        public List<SetAgentProfileDefaultBindingCommand> BindingCommands { get; } = [];
        public List<ClearAgentProfileDefaultBindingCommand> ClearBindingCommands { get; } = [];

        public Task<DispatchAdmission> DispatchCreateAsync(
            CreateAgentProfileCommand command,
            CancellationToken ct = default)
        {
            CreateCommands.Add(command.Clone());
            return Admission(AgentProfileActorIds.Profile(command.ProfileId), command.Operation);
        }

        public Task<DispatchAdmission> DispatchInitializeAsync(
            string profileActorId,
            InitializeAgentProfileCommand command,
            CancellationToken ct = default) => Admission(profileActorId, command.Operation);

        public Task<DispatchAdmission> DispatchUpdateDraftAsync(
            string profileActorId,
            UpdateAgentProfileDraftCommand command,
            CancellationToken ct = default)
        {
            DraftCommands.Add(command.Clone());
            return Admission(profileActorId, command.Operation);
        }

        public Task<DispatchAdmission> DispatchPublishAsync(
            string profileActorId,
            PublishAgentProfileCommand command,
            CancellationToken ct = default)
        {
            PublishCommands.Add(command.Clone());
            return Admission(profileActorId, command.Operation);
        }

        public Task<DispatchAdmission> DispatchSetDefaultBindingAsync(
            SetAgentProfileDefaultBindingCommand command,
            CancellationToken ct = default)
        {
            BindingCommands.Add(command.Clone());
            return Admission("namespace-actor", command.Operation);
        }

        public Task<DispatchAdmission> DispatchClearDefaultBindingAsync(
            ClearAgentProfileDefaultBindingCommand command,
            CancellationToken ct = default)
        {
            ClearBindingCommands.Add(command.Clone());
            return Admission("namespace-actor", command.Operation);
        }

        private static Task<DispatchAdmission> Admission(string actorId, AgentProfileOperationFact operation) =>
            Task.FromResult(new DispatchAdmission(
                true,
                operation.CommandId,
                DateTimeOffset.UtcNow,
                actorId,
                operation.CorrelationId));
    }

    private sealed class RecordingSealer : IAgentProfileSkillSealer
    {
        public AgentProfileIdentity? Identity { get; private set; }
        public AgentProfileDraft? Draft { get; private set; }
        public AgentProfileSealingContext? Context { get; private set; }

        public Task<AgentProfileSealingResult> ResolveAndSealAsync(
            AgentProfileIdentity identity,
            AgentProfileDraft draft,
            AgentProfileSealingContext context,
            CancellationToken ct = default)
        {
            Identity = identity.Clone();
            Draft = draft.Clone();
            Context = context;
            return Task.FromResult(AgentProfileSealingResult.Success(
                AgentProfileDeterminism.BuildPublishedSnapshot(
                    identity,
                    draft,
                    context.CurrentDraftRevision,
                    context.NextPublishedRevision,
                    context.PublishedAt)));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
