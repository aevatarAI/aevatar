using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class AgentProfileQueryApplicationServiceTests
{
    private const string ProfileId = "prof-alpha";
    private const string ScopeId = "scope-gamma";

    [Fact]
    public async Task GetOwnedAsync_ShouldReturnRawDraftWithoutReadingOrExposingSealedContent()
    {
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = UserEntry() };
        var managementPort = new RecordingManagementQueryPort { Result = Management() };
        var executionPort = new RecordingExecutionQueryPort { Result = Execution() };
        var service = new AgentProfileQueryApplicationService(
            namespacePort,
            managementPort,
            executionPort);

        var result = await service.GetOwnedAsync(Caller(), "profile-alpha");

        result.Should().NotBeNull();
        result!.Draft.Instructions.Should().Be("owner-authored-draft-secret");
        result.Draft.SkillBindings.Should().ContainSingle()
            .Which.Skill.Should().BeEquivalentTo(ExactReference());
        result.GetType().GetProperty("Snapshot").Should().BeNull();
        result.ToString().Should().NotContain("sealed-package-secret");
        namespacePort.OwnedCalls.Should().ContainSingle();
        managementPort.ProfileIds.Should().Equal(ProfileId);
        executionPort.ProfileIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(InaccessibleKind.MissingNamespace)]
    [InlineData(InaccessibleKind.InactiveNamespace)]
    [InlineData(InaccessibleKind.WrongOwner)]
    [InlineData(InaccessibleKind.WrongScope)]
    [InlineData(InaccessibleKind.MissingManagement)]
    [InlineData(InaccessibleKind.MismatchedManagementIdentity)]
    public async Task GetOwnedAsync_InaccessibleResource_ShouldReturnNull(InaccessibleKind kind)
    {
        var entry = UserEntry();
        var management = Management();
        if (kind == InaccessibleKind.InactiveNamespace)
            entry = entry with { Status = AgentProfileProvisioningStatus.Provisioning };
        else if (kind == InaccessibleKind.WrongOwner)
            entry = entry with
            {
                Owner = new AgentProfileOwnerIdentity
                {
                    User = new AgentProfileUserOwnerIdentity
                    {
                        IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
                        SubjectId = "subject-other",
                    },
                },
            };
        else if (kind == InaccessibleKind.WrongScope)
            entry = entry with { OwningScopeId = "scope-other" };
        else if (kind == InaccessibleKind.MismatchedManagementIdentity)
        {
            var mismatchedIdentity = management.Identity;
            mismatchedIdentity.ProfileId = "prof-other";
            management = management with { Identity = mismatchedIdentity };
        }

        var service = new AgentProfileQueryApplicationService(
            new RecordingNamespaceQueryPort
            {
                OwnedResult = kind == InaccessibleKind.MissingNamespace ? null : entry,
            },
            new RecordingManagementQueryPort
            {
                Result = kind == InaccessibleKind.MissingManagement ? null : management,
            },
            new RecordingExecutionQueryPort());

        var result = await service.GetOwnedAsync(Caller(), "profile-alpha");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetOwnedAsync_ShouldNotGrantManagementForSystemEntry()
    {
        var namespacePort = new RecordingNamespaceQueryPort { OwnedResult = SystemEntry() };
        var managementPort = new RecordingManagementQueryPort { Result = Management() };
        var service = new AgentProfileQueryApplicationService(
            namespacePort,
            managementPort,
            new RecordingExecutionQueryPort());

        var result = await service.GetOwnedAsync(Caller(), "studio");

        result.Should().BeNull();
        managementPort.ProfileIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveVisibleAsync_ShouldProjectOnlySafePublishedSummaryAndAvailability()
    {
        var namespacePort = new RecordingNamespaceQueryPort { ReferenceResult = UserEntry() };
        var managementPort = new RecordingManagementQueryPort { Result = Management() };
        var executionPort = new RecordingExecutionQueryPort { Result = Execution() };
        var service = new AgentProfileQueryApplicationService(
            namespacePort,
            managementPort,
            executionPort);

        var result = await service.ResolveVisibleAsync(Caller(), Reference());

        result.Should().NotBeNull();
        result!.Reference.Should().BeEquivalentTo(Reference());
        result.DisplayName.Should().Be("Published alpha");
        result.Purpose.Should().Be("Safe published purpose");
        result.PublishedRevision.Should().Be(3);
        result.Available.Should().BeTrue();
        result.GetType().GetProperties().Select(static property => property.Name)
            .Should().BeEquivalentTo(
                "Reference",
                "DisplayName",
                "Purpose",
                "PublishedRevision",
                "Available");
        result.ToString().Should().NotContain("owner-authored-draft-secret");
        result.ToString().Should().NotContain("sealed-package-secret");
        namespacePort.ReferenceCalls.Should().ContainSingle();
        executionPort.ProfileIds.Should().Equal(ProfileId);
        managementPort.ProfileIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveVisibleAsync_CrossScopeUserProfile_ShouldRemainHiddenWithoutExecutionLookup()
    {
        var entry = Entry(ProfileId, Identity().Owner, "scope-other", Reference());
        var executionPort = new RecordingExecutionQueryPort
        {
            Result = new AgentProfileExecutionSnapshot(
                14,
                "profile-event-other",
                PublishedSnapshot(new AgentProfileIdentity
                {
                    ProfileId = ProfileId,
                    Owner = entry.Owner,
                    OwningScopeId = entry.OwningScopeId,
                    Reference = entry.Reference,
                })),
        };
        var service = new AgentProfileQueryApplicationService(
            new RecordingNamespaceQueryPort { ReferenceResult = entry },
            new RecordingManagementQueryPort(),
            executionPort);

        var result = await service.ResolveVisibleAsync(Caller(), Reference());

        result.Should().BeNull();
        executionPort.ProfileIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PublishedDisplayFact.DisplayName)]
    [InlineData(PublishedDisplayFact.Purpose)]
    public async Task ResolveVisibleAsync_DriftedPublishedDisplayFact_ShouldReportUnavailable(
        PublishedDisplayFact driftedFact)
    {
        var entry = UserEntry();
        var summary = entry.PublishedSummary!;
        if (driftedFact == PublishedDisplayFact.DisplayName)
            summary.DisplayName = "Forged display name";
        else
            summary.Purpose = "Forged purpose";
        entry = entry with { PublishedSummary = summary };
        var service = new AgentProfileQueryApplicationService(
            new RecordingNamespaceQueryPort { ReferenceResult = entry },
            new RecordingManagementQueryPort(),
            new RecordingExecutionQueryPort { Result = Execution() });

        var result = await service.ResolveVisibleAsync(Caller(), Reference());

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be(summary.DisplayName);
        result.Purpose.Should().Be(summary.Purpose);
        result.Available.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveVisibleAsync_MissingOrLaggingExecutionSnapshot_ShouldReportUnavailable()
    {
        var entry = UserEntry();
        var missingExecution = new AgentProfileQueryApplicationService(
            new RecordingNamespaceQueryPort { ReferenceResult = entry },
            new RecordingManagementQueryPort(),
            new RecordingExecutionQueryPort());
        var laggingSnapshot = Execution().Snapshot;
        laggingSnapshot.PublishedRevision = 2;
        var lagging = new AgentProfileExecutionSnapshot(
            13,
            "profile-event-13",
            laggingSnapshot);
        var laggingExecution = new AgentProfileQueryApplicationService(
            new RecordingNamespaceQueryPort { ReferenceResult = entry },
            new RecordingManagementQueryPort(),
            new RecordingExecutionQueryPort { Result = lagging });

        var missingResult = await missingExecution.ResolveVisibleAsync(Caller(), Reference());
        var laggingResult = await laggingExecution.ResolveVisibleAsync(Caller(), Reference());

        missingResult.Should().NotBeNull();
        missingResult!.Available.Should().BeFalse();
        laggingResult.Should().NotBeNull();
        laggingResult!.Available.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveVisibleAsync_SystemProfile_ShouldBeVisibleWithoutGrantingManagement()
    {
        var entry = SystemEntry();
        var execution = SystemExecution();
        var namespacePort = new RecordingNamespaceQueryPort
        {
            ReferenceResult = entry,
            OwnedResult = entry,
        };
        var managementPort = new RecordingManagementQueryPort();
        var service = new AgentProfileQueryApplicationService(
            namespacePort,
            managementPort,
            new RecordingExecutionQueryPort { Result = execution });

        var discovery = await service.ResolveVisibleAsync(Caller(), SystemReference());
        var management = await service.GetOwnedAsync(Caller(), "studio");

        discovery.Should().NotBeNull();
        discovery!.Reference.Should().BeEquivalentTo(SystemReference());
        discovery.Available.Should().BeTrue();
        management.Should().BeNull();
        managementPort.ProfileIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveVisibleAsync_UserOwnedSystemReference_ShouldReturnNull()
    {
        var entry = UserEntry();
        var summary = entry.PublishedSummary!;
        summary.Reference = SystemReference();
        entry = entry with
        {
            Reference = SystemReference(),
            PublishedSummary = summary,
        };
        var executionPort = new RecordingExecutionQueryPort { Result = Execution() };
        var service = new AgentProfileQueryApplicationService(
            new RecordingNamespaceQueryPort { ReferenceResult = entry },
            new RecordingManagementQueryPort(),
            executionPort);

        var result = await service.ResolveVisibleAsync(Caller(), SystemReference());

        result.Should().BeNull();
        executionPort.ProfileIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveVisibleAsync_MalformedPublishedSummary_ShouldReturnNull()
    {
        var entry = UserEntry();
        var summary = entry.PublishedSummary!;
        summary.DisplayName = string.Empty;
        entry = entry with { PublishedSummary = summary };
        var executionPort = new RecordingExecutionQueryPort { Result = Execution() };
        var service = new AgentProfileQueryApplicationService(
            new RecordingNamespaceQueryPort { ReferenceResult = entry },
            new RecordingManagementQueryPort(),
            executionPort);

        var result = await service.ResolveVisibleAsync(Caller(), Reference());

        result.Should().BeNull();
        executionPort.ProfileIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveVisibleAsync_ForgedMatchingSnapshotDigests_ShouldReportUnavailable()
    {
        var forgedDigest = Digest(0x7f);
        var entry = UserEntry();
        var summary = entry.PublishedSummary!;
        summary.SnapshotSha256 = forgedDigest;
        entry = entry with { PublishedSummary = summary };
        var snapshot = Execution().Snapshot;
        snapshot.SnapshotSha256 = forgedDigest;
        var execution = new AgentProfileExecutionSnapshot(
            14,
            "profile-event-14",
            snapshot);
        var service = new AgentProfileQueryApplicationService(
            new RecordingNamespaceQueryPort { ReferenceResult = entry },
            new RecordingManagementQueryPort(),
            new RecordingExecutionQueryPort { Result = execution });

        var result = await service.ResolveVisibleAsync(Caller(), Reference());

        result.Should().NotBeNull();
        result!.Available.Should().BeFalse();
    }

    [Theory]
    [InlineData(InaccessibleDiscoveryKind.Missing)]
    [InlineData(InaccessibleDiscoveryKind.Inactive)]
    [InlineData(InaccessibleDiscoveryKind.Unpublished)]
    public async Task ResolveVisibleAsync_InaccessibleResource_ShouldReturnNull(
        InaccessibleDiscoveryKind kind)
    {
        var entry = UserEntry();
        if (kind == InaccessibleDiscoveryKind.Inactive)
            entry = entry with { Status = AgentProfileProvisioningStatus.Failed };
        else if (kind == InaccessibleDiscoveryKind.Unpublished)
            entry = entry with { PublishedSummary = null };
        var service = new AgentProfileQueryApplicationService(
            new RecordingNamespaceQueryPort
            {
                ReferenceResult = kind == InaccessibleDiscoveryKind.Missing ? null : entry,
            },
            new RecordingManagementQueryPort(),
            new RecordingExecutionQueryPort { Result = Execution() });

        var result = await service.ResolveVisibleAsync(Caller(), Reference());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveVisibleAsync_ShouldPreserveCallerCancellation()
    {
        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new AgentProfileQueryApplicationService(
            new RecordingNamespaceQueryPort { ReferenceResult = UserEntry() },
            new RecordingManagementQueryPort(),
            new RecordingExecutionQueryPort { Result = Execution() });

        var act = () => service.ResolveVisibleAsync(Caller(), Reference(), cancellation.Token);

        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cancellation.Token);
    }

    private static AgentProfileCallerContext Caller() =>
        new(
            new AgentProfileUserOwnerIdentity
            {
                IdentityProvider = AgentProfilePolicies.NyxIdIdentityProvider,
                SubjectId = "subject-alpha",
            },
            ScopeId,
            "owner-alpha",
            "token-alpha");

    private static AgentProfileReference Reference() =>
        new() { OwnerHandle = "owner-alpha", ProfileSlug = "profile-alpha" };

    private static AgentProfileReference SystemReference() =>
        new() { OwnerHandle = AgentProfilePolicies.SystemOwnerHandle, ProfileSlug = "studio" };

    private static AgentProfileIdentity Identity() =>
        new()
        {
            ProfileId = ProfileId,
            Owner = new AgentProfileOwnerIdentity { User = Caller().Owner },
            OwningScopeId = ScopeId,
            Reference = Reference(),
        };

    private static AgentProfileNamespaceEntrySnapshot UserEntry() =>
        Entry(ProfileId, Identity().Owner, ScopeId, Reference());

    private static AgentProfileNamespaceEntrySnapshot SystemEntry()
    {
        var owner = new AgentProfileOwnerIdentity
        {
            System = new AgentProfileSystemOwnerIdentity
            {
                PlatformId = AgentProfilePolicies.AevatarPlatformId,
            },
        };
        return Entry("prof-system-studio", owner, string.Empty, SystemReference());
    }

    private static AgentProfileNamespaceEntrySnapshot Entry(
        string profileId,
        AgentProfileOwnerIdentity owner,
        string scopeId,
        AgentProfileReference reference)
    {
        var identity = new AgentProfileIdentity
        {
            ProfileId = profileId,
            Owner = owner,
            OwningScopeId = scopeId,
            Reference = reference,
        };
        var snapshot = PublishedSnapshot(identity);
        return new AgentProfileNamespaceEntrySnapshot(
            9,
            "namespace-event-9",
            profileId,
            reference,
            owner,
            scopeId,
            AgentProfileProvisioningStatus.Active,
            new AgentProfilePublishedSummary
            {
                Reference = reference,
                DisplayName = reference.OwnerHandle == AgentProfilePolicies.SystemOwnerHandle
                    ? "Studio"
                    : "Published alpha",
                Purpose = "Safe published purpose",
                PublishedRevision = 3,
                SnapshotSha256 = snapshot.SnapshotSha256,
            });
    }

    private static AgentProfileManagementSnapshot Management()
    {
        var draft = AgentProfileDeterminism.NormalizeContent(new AgentProfileContent
        {
            DisplayName = "Draft alpha",
            Purpose = "Draft purpose",
            Instructions = "owner-authored-draft-secret",
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.InheritRouteMaximum,
            },
            SkillBindings =
            {
                new AgentProfileSkillBinding
                {
                    BindingId = "bind-alpha",
                    ActivationMode = AgentProfileSkillActivationMode.Routed,
                    Skill = ExactReference(),
                    RoutingPolicy = RoutingPolicy("bind-alpha").Clone(),
                },
            },
        });
        return new AgentProfileManagementSnapshot(
            14,
            "profile-event-14",
            Identity(),
            draft,
            5,
            AgentProfileDeterminism.ComputeDraftSha256(draft),
            3,
            PublishedSnapshot(Identity()).SnapshotSha256,
            Digest(0x22),
            null);
    }

    private static AgentProfileExecutionSnapshot Execution() =>
        new(
            14,
            "profile-event-14",
            PublishedSnapshot(Identity()));

    private static AgentProfileExecutionSnapshot SystemExecution()
    {
        var identity = new AgentProfileIdentity
        {
            ProfileId = "prof-system-studio",
            Owner = SystemEntry().Owner,
            OwningScopeId = string.Empty,
            Reference = SystemReference(),
        };
        return new AgentProfileExecutionSnapshot(14, "system-event-14", PublishedSnapshot(identity));
    }

    private static AgentProfilePublishedSnapshot PublishedSnapshot(AgentProfileIdentity identity)
    {
        var normalizedContent = AgentProfileDeterminism.NormalizeContent(new AgentProfileContent
        {
            DisplayName = identity.Reference.OwnerHandle == AgentProfilePolicies.SystemOwnerHandle
                ? "Studio"
                : "Published alpha",
            Purpose = "Safe published purpose",
            Instructions = "published-profile-secret",
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.InheritRouteMaximum,
            },
            SkillBindings =
            {
                new AgentProfileSkillBinding
                {
                    BindingId = "bind-alpha",
                    ActivationMode = AgentProfileSkillActivationMode.Routed,
                    Skill = ExactReference(),
                    RoutingPolicy = RoutingPolicy("bind-alpha").Clone(),
                },
            },
        });
        var binding = normalizedContent.SkillBindings.Single();
        var sealedSkill = new SealedAgentProfileSkill
        {
            ExactReference = binding.Skill.Clone(),
            Package = new ResolvedOrnnSkillPackage
            {
                SkillGuid = binding.Skill.SkillGuid,
                LiteralVersion = binding.Skill.LiteralVersion,
                CanonicalName = binding.Skill.ExpectedName,
                PublisherId = binding.Skill.ExpectedPublisherId,
                UpstreamSkillHash = "upstream-hash-alpha",
                Instructions = "sealed-package-secret",
            },
        };
        sealedSkill.ContentSha256 = AgentProfileDeterminism.ComputeSkillContentSha256(sealedSkill);
        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = identity,
            DisplayName = normalizedContent.DisplayName,
            Purpose = normalizedContent.Purpose,
            Instructions = normalizedContent.Instructions,
            ToolPolicy = normalizedContent.ToolPolicy.Clone(),
            RecoveryToolPolicy = normalizedContent.RecoveryToolPolicy.Clone(),
            SkillBindings =
            {
                new SealedAgentProfileSkillBinding
                {
                    BindingId = binding.BindingId,
                    ActivationMode = binding.ActivationMode,
                    Skill = sealedSkill,
                    RoutingPolicy = binding.RoutingPolicy?.Clone(),
                },
            },
            PublishedRevision = 3,
            SourceDraftSha256 = Digest(0x22),
        };
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
        return AgentProfileDeterminism.NormalizePublishedSnapshot(snapshot);
    }

    private static ExactOrnnSkillReference ExactReference() =>
        new()
        {
            SkillGuid = "00000000-0000-0000-0000-000000000001",
            LiteralVersion = "1.4",
            ExpectedName = "skill-alpha",
            ExpectedPublisherId = "publisher-alpha",
        };

    private static AgentProfileSkillRoutingPolicy RoutingPolicy(string bindingId) =>
        new()
        {
            IntentId = bindingId,
            RoutingDescription = $"Route requests for {bindingId}.",
            TaskToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
            },
            SideEffectClass = AgentProfileSkillSideEffectClass.ReadOnly,
            ExplicitTriggerAliases = { bindingId },
        };

    private static ByteString Digest(byte value) =>
        ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());

    public enum InaccessibleKind
    {
        MissingNamespace,
        InactiveNamespace,
        WrongOwner,
        WrongScope,
        MissingManagement,
        MismatchedManagementIdentity,
    }

    public enum InaccessibleDiscoveryKind
    {
        Missing,
        Inactive,
        Unpublished,
    }

    public enum PublishedDisplayFact
    {
        DisplayName,
        Purpose,
    }

    private sealed class RecordingNamespaceQueryPort : IAgentProfileNamespaceQueryPort
    {
        public AgentProfileNamespaceEntrySnapshot? OwnedResult { get; init; }
        public AgentProfileNamespaceEntrySnapshot? ReferenceResult { get; init; }
        public List<(AgentProfileOwnerIdentity Owner, string ScopeId, string Slug)> OwnedCalls { get; } = [];
        public List<AgentProfileReference> ReferenceCalls { get; } = [];

        public Task<AgentProfileNamespaceEntrySnapshot?> GetOwnedAsync(
            AgentProfileOwnerIdentity owner,
            string owningScopeId,
            string profileSlug,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            OwnedCalls.Add((owner.Clone(), owningScopeId, profileSlug));
            return Task.FromResult(OwnedResult?.DeepClone());
        }

        public Task<AgentProfileNamespaceEntrySnapshot?> GetByReferenceAsync(
            AgentProfileReference reference,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReferenceCalls.Add(reference.Clone());
            return Task.FromResult(ReferenceResult?.DeepClone());
        }
    }

    private sealed class RecordingManagementQueryPort : IAgentProfileManagementQueryPort
    {
        public AgentProfileManagementSnapshot? Result { get; init; }
        public List<string> ProfileIds { get; } = [];

        public Task<AgentProfileManagementSnapshot?> GetAsync(
            string profileId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ProfileIds.Add(profileId);
            return Task.FromResult(Result?.DeepClone());
        }
    }

    private sealed class RecordingExecutionQueryPort : IAgentProfileExecutionSnapshotQueryPort
    {
        public AgentProfileExecutionSnapshot? Result { get; init; }
        public List<string> ProfileIds { get; } = [];

        public Task<AgentProfileExecutionSnapshot?> GetAsync(
            string profileId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ProfileIds.Add(profileId);
            return Task.FromResult(Result?.DeepClone());
        }
    }
}
