using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using Aevatar.Mainnet.Host.Api.Profiles;
using FluentAssertions;
using Google.Protobuf;
using ProfileToolPolicy = Aevatar.GAgentService.Abstractions.AgentProfiles.AgentProfileToolPolicy;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetNyxIdChatAgentProfileBindingSourceTests
{
    private const string OpaqueProfileId = "profile-7f4b91";
    private const string RouteToolSetName = "workspace.route";

    [Fact]
    public async Task Resolve_NotSelected_ShouldNotQueryProfileReadModels()
    {
        var namespaceQuery = new RecordingNamespaceQueryPort();
        var executionQuery = new RecordingExecutionQueryPort();
        var source = CreateSource(
            releaseSpec: null,
            namespaceQuery,
            executionQuery);

        var result = await source.ResolveForNewConversationAsync(
            "conversation-a",
            RouteToolSetName);

        result.Status.Should().Be(NyxIdChatAgentProfileBindingStatus.NotSelected);
        result.Binding.Should().BeNull();
        namespaceQuery.References.Should().BeEmpty();
        executionQuery.ProfileIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_Bound_ShouldUseTypedReferenceAndMapCompleteImmutableBinding()
    {
        var snapshot = BuildPublishedSnapshot();
        var releaseSpec = BuildReleaseSpec(snapshot);
        var namespaceQuery = new RecordingNamespaceQueryPort
        {
            Result = BuildNamespaceEntry(snapshot),
        };
        var executionQuery = new RecordingExecutionQueryPort
        {
            Result = new AgentProfileExecutionSnapshot(17, "profile-event-17", snapshot),
        };
        var source = CreateSource(releaseSpec, namespaceQuery, executionQuery);

        var result = await source.ResolveForNewConversationAsync(
            "conversation-a",
            RouteToolSetName);

        result.Status.Should().Be(NyxIdChatAgentProfileBindingStatus.Bound);
        result.Binding.Should().NotBeNull();
        namespaceQuery.References.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new AgentProfileReference { OwnerHandle = "system", ProfileSlug = "nyxid-chat" });
        executionQuery.ProfileIds.Should().Equal(OpaqueProfileId);
        var binding = result.Binding!;
        AgentProfileExecutionBindingCodec.Verify(binding).Should().BeTrue();
        binding.Source.ProfileId.Should().Be(OpaqueProfileId);
        binding.Source.StateVersion.Should().Be(17);
        binding.Source.PublishedRevision.Should().Be(snapshot.PublishedRevision);
        binding.Source.PublishedSnapshotSha256.Should().Equal(snapshot.SnapshotSha256);
        binding.Admission.RolloutRelease.Should().Be(releaseSpec.ReleaseId);
        binding.Admission.RolloutStage.Should().Be(releaseSpec.Stage);
        binding.Admission.RouteToolSetRef.Should().Be(RouteToolSetName);
        binding.Admission.AdmissionSha256.Should().Equal(
            MainnetAgentProfileRolloutSelector.ComputeAdmissionSha256(releaseSpec));
        binding.ProfileInstructions.Should().Be(snapshot.Instructions);
        binding.EffectiveMaximumToolPolicy.ToolNames.Should().Equal("recovery", "task");
        binding.EffectiveRecoveryToolPolicy.ToolNames.Should().Equal("recovery");
        binding.RuntimeBounds.MaxPlanSteps.Should().Be(4);
        binding.Members.Should().ContainSingle();
        binding.Members[0].IntentId.Should().Be("service_call");
        binding.Members[0].ActivationMode.Should()
            .Be(AgentProfileExecutionMemberActivationMode.Routed);
        binding.Members[0].InstructionBody.Should().Be("Use the sealed service call procedure.");
        binding.Members[0].SkillProvenance.ExactSkillRef.Guid.Should()
            .Be(snapshot.SkillBindings[0].Skill.ExactReference.SkillGuid);
    }

    [Fact]
    public async Task Resolve_DefaultBinding_ShouldPreserveAuthoritativeActivation()
    {
        var snapshot = BuildPublishedSnapshot(
            AgentProfileSkillActivationMode.DefaultForUnmatchedTurn);
        var source = CreateSource(
            BuildReleaseSpec(snapshot),
            new RecordingNamespaceQueryPort { Result = BuildNamespaceEntry(snapshot) },
            new RecordingExecutionQueryPort
            {
                Result = new AgentProfileExecutionSnapshot(17, "profile-event-17", snapshot),
            });

        var result = await source.ResolveForNewConversationAsync(
            "conversation-a",
            RouteToolSetName);

        result.Status.Should().Be(NyxIdChatAgentProfileBindingStatus.Bound);
        result.Binding!.Members.Should().ContainSingle();
        result.Binding.Members[0].ActivationMode.Should()
            .Be(AgentProfileExecutionMemberActivationMode.DefaultForUnmatchedTurn);
        AgentProfileExecutionBindingCodec.Verify(result.Binding).Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_AlwaysBinding_ShouldPreserveInstructionsWithoutRoutingAuthority()
    {
        var snapshot = BuildPublishedSnapshot(AgentProfileSkillActivationMode.Always);
        var source = CreateSource(
            BuildReleaseSpec(snapshot),
            new RecordingNamespaceQueryPort { Result = BuildNamespaceEntry(snapshot) },
            new RecordingExecutionQueryPort
            {
                Result = new AgentProfileExecutionSnapshot(17, "profile-event-17", snapshot),
            });

        var result = await source.ResolveForNewConversationAsync(
            "conversation-a",
            RouteToolSetName);

        result.Status.Should().Be(NyxIdChatAgentProfileBindingStatus.Bound);
        result.Binding!.Members.Should().ContainSingle();
        var member = result.Binding.Members[0];
        member.ActivationMode.Should().Be(AgentProfileExecutionMemberActivationMode.Always);
        member.InstructionBody.Should().Be("Use the sealed service call procedure.");
        member.SkillProvenance.ExactSkillRef.Guid.Should()
            .Be(snapshot.SkillBindings[0].Skill.ExactReference.SkillGuid);
        member.IntentId.Should().BeEmpty();
        member.RoutingDescription.Should().BeEmpty();
        member.ExplicitTriggerAliases.Should().BeEmpty();
        member.TaskToolPolicy.Should().BeNull();
        member.SideEffectClass.Should().Be(AgentProfileSideEffectClass.Unspecified);
        AgentProfileExecutionBindingCodec.Verify(result.Binding).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolve_MissingReadModel_ShouldReturnProfileUnavailable(
        bool namespaceMissing)
    {
        var snapshot = BuildPublishedSnapshot();
        var namespaceQuery = new RecordingNamespaceQueryPort
        {
            Result = namespaceMissing ? null : BuildNamespaceEntry(snapshot),
        };
        var executionQuery = new RecordingExecutionQueryPort
        {
            Result = namespaceMissing
                ? new AgentProfileExecutionSnapshot(17, "profile-event-17", snapshot)
                : null,
        };
        var source = CreateSource(BuildReleaseSpec(snapshot), namespaceQuery, executionQuery);

        var result = await source.ResolveForNewConversationAsync(
            "conversation-a",
            RouteToolSetName);

        result.Status.Should().Be(NyxIdChatAgentProfileBindingStatus.ProfileUnavailable);
        result.Binding.Should().BeNull();
        namespaceQuery.References.Should().ContainSingle();
        executionQuery.ProfileIds.Should().HaveCount(namespaceMissing ? 0 : 1);
    }

    [Fact]
    public async Task Resolve_StaleNamespaceAndExecutionReadModels_ShouldReturnProfileUnavailable()
    {
        var snapshot = BuildPublishedSnapshot();
        var staleNamespace = BuildNamespaceEntry(snapshot);
        staleNamespace = staleNamespace with
        {
            PublishedSummary = new AgentProfilePublishedSummary
            {
                Reference = staleNamespace.Reference,
                DisplayName = "NyxID chat",
                PublishedRevision = snapshot.PublishedRevision - 1,
                SnapshotSha256 = Digest(0x42),
            },
        };
        var source = CreateSource(
            BuildReleaseSpec(snapshot),
            new RecordingNamespaceQueryPort { Result = staleNamespace },
            new RecordingExecutionQueryPort
            {
                Result = new AgentProfileExecutionSnapshot(17, "profile-event-17", snapshot),
            });

        var result = await source.ResolveForNewConversationAsync(
            "conversation-a",
            RouteToolSetName);

        result.Status.Should().Be(NyxIdChatAgentProfileBindingStatus.ProfileUnavailable);
        result.Binding.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_BadPublishedSnapshotDigest_ShouldReturnAdmissionMismatch()
    {
        var snapshot = BuildPublishedSnapshot();
        var releaseSpec = BuildReleaseSpec(snapshot);
        snapshot.Instructions = "tampered after publication";
        var source = CreateSource(
            releaseSpec,
            new RecordingNamespaceQueryPort { Result = BuildNamespaceEntry(snapshot) },
            new RecordingExecutionQueryPort
            {
                Result = new AgentProfileExecutionSnapshot(17, "profile-event-17", snapshot),
            });

        var result = await source.ResolveForNewConversationAsync(
            "conversation-a",
            RouteToolSetName);

        result.Status.Should().Be(NyxIdChatAgentProfileBindingStatus.AdmissionMismatch);
        result.Binding.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_NamespaceIdentityMismatch_ShouldReturnAdmissionMismatchWithoutExecutionRead()
    {
        var snapshot = BuildPublishedSnapshot();
        var namespaceEntry = BuildNamespaceEntry(snapshot) with
        {
            Reference = new AgentProfileReference
            {
                OwnerHandle = "system",
                ProfileSlug = "other-profile",
            },
        };
        var executionQuery = new RecordingExecutionQueryPort
        {
            Result = new AgentProfileExecutionSnapshot(17, "profile-event-17", snapshot),
        };
        var source = CreateSource(
            BuildReleaseSpec(snapshot),
            new RecordingNamespaceQueryPort { Result = namespaceEntry },
            executionQuery);

        var result = await source.ResolveForNewConversationAsync(
            "conversation-a",
            RouteToolSetName);

        result.Status.Should().Be(NyxIdChatAgentProfileBindingStatus.AdmissionMismatch);
        result.Binding.Should().BeNull();
        executionQuery.ProfileIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_ExactClosureMismatch_ShouldReturnAdmissionMismatch()
    {
        var snapshot = BuildPublishedSnapshot();
        var releaseSpec = BuildReleaseSpec(snapshot);
        releaseSpec.ExpectedExactSkillClosure[0].SkillGuid =
            "22222222-2222-2222-2222-222222222222";
        var source = CreateSource(
            releaseSpec,
            new RecordingNamespaceQueryPort { Result = BuildNamespaceEntry(snapshot) },
            new RecordingExecutionQueryPort
            {
                Result = new AgentProfileExecutionSnapshot(17, "profile-event-17", snapshot),
            });

        var result = await source.ResolveForNewConversationAsync(
            "conversation-a",
            RouteToolSetName);

        result.Status.Should().Be(NyxIdChatAgentProfileBindingStatus.AdmissionMismatch);
        result.Binding.Should().BeNull();
    }

    private static MainnetNyxIdChatAgentProfileBindingSource CreateSource(
        AgentProfileRolloutReleaseSpec? releaseSpec,
        RecordingNamespaceQueryPort namespaceQuery,
        RecordingExecutionQueryPort executionQuery) =>
        new(
            new MainnetAgentProfileRolloutSelector(releaseSpec),
            namespaceQuery,
            executionQuery);

    private static AgentProfileRolloutReleaseSpec BuildReleaseSpec(
        AgentProfilePublishedSnapshot snapshot)
    {
        var spec = new AgentProfileRolloutReleaseSpec
        {
            ReleaseId = "nyxid-chat-2026-07-25",
            Stage = "canary",
            ProfileReference = new AgentProfileReference
            {
                OwnerHandle = "system",
                ProfileSlug = "nyxid-chat",
            },
            ActivationMode = AgentProfileRolloutActivationMode.Enforced,
            CohortSalt = "nyxid-chat-canary-1",
            CohortBasisPoints = 10_000,
            ExpectedPublishedRevision = snapshot.PublishedRevision,
            ExpectedPublishedSnapshotSha256 = snapshot.SnapshotSha256,
            RuntimeBounds = new AgentProfileRolloutRuntimeBounds
            {
                MaxPlanSteps = 4,
                HandoffTtlSeconds = 900,
                ClassifierTimeoutMs = 600,
                MaxSelectedSkillBytes = 24_576,
            },
        };
        spec.ExpectedExactSkillClosure.Add(
            snapshot.SkillBindings.Select(static binding => binding.Skill.ExactReference.Clone()));
        return spec;
    }

    private static AgentProfileNamespaceEntrySnapshot BuildNamespaceEntry(
        AgentProfilePublishedSnapshot snapshot) =>
        new(
            11,
            "namespace-event-11",
            OpaqueProfileId,
            snapshot.Identity.Reference,
            snapshot.Identity.Owner,
            snapshot.Identity.OwningScopeId,
            AgentProfileProvisioningStatus.Active,
            new AgentProfilePublishedSummary
            {
                Reference = snapshot.Identity.Reference,
                DisplayName = snapshot.DisplayName,
                Purpose = snapshot.Purpose,
                PublishedRevision = snapshot.PublishedRevision,
                SnapshotSha256 = snapshot.SnapshotSha256,
            });

    private static AgentProfilePublishedSnapshot BuildPublishedSnapshot(
        AgentProfileSkillActivationMode activationMode = AgentProfileSkillActivationMode.Routed)
    {
        var exactReference = new ExactOrnnSkillReference
        {
            SkillGuid = "11111111-1111-1111-1111-111111111111",
            LiteralVersion = "1.2",
            ExpectedName = "service-call",
            ExpectedPublisherId = "publisher-alpha",
        };
        var sealedSkill = new SealedAgentProfileSkill
        {
            ExactReference = exactReference,
            Package = new ResolvedOrnnSkillPackage
            {
                SkillGuid = exactReference.SkillGuid,
                LiteralVersion = exactReference.LiteralVersion,
                CanonicalName = exactReference.ExpectedName,
                PublisherId = exactReference.ExpectedPublisherId,
                UpstreamSkillHash = "upstream-sha-alpha",
                Instructions = "Use the sealed service call procedure.",
            },
        };
        sealedSkill.ContentSha256 = AgentProfileDeterminism.ComputeSkillContentSha256(sealedSkill);
        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = new AgentProfileIdentity
            {
                ProfileId = OpaqueProfileId,
                Owner = new AgentProfileOwnerIdentity
                {
                    System = new AgentProfileSystemOwnerIdentity { PlatformId = "aevatar" },
                },
                OwningScopeId = string.Empty,
                Reference = new AgentProfileReference
                {
                    OwnerHandle = "system",
                    ProfileSlug = "nyxid-chat",
                },
            },
            DisplayName = "NyxID chat",
            Purpose = "System NyxID conversation profile",
            Instructions = "Follow the published NyxID profile.",
            ToolPolicy = ExplicitPolicy("recovery", "task"),
            RecoveryToolPolicy = ExplicitPolicy("recovery"),
            PublishedRevision = 7,
            SourceDraftSha256 = Digest(0x31),
        };
        var skillBinding = new SealedAgentProfileSkillBinding
        {
            BindingId = "service-call",
            ActivationMode = activationMode,
            Skill = sealedSkill,
        };
        if (activationMode != AgentProfileSkillActivationMode.Always)
        {
            skillBinding.RoutingPolicy = new AgentProfileSkillRoutingPolicy
            {
                IntentId = "service_call",
                RoutingDescription = "Call one exact connected service.",
                TaskToolPolicy = ExplicitPolicy("task"),
                SideEffectClass = AgentProfileSkillSideEffectClass.ServiceCall,
                ExplicitTriggerAliases = { "call-service" },
            };
        }
        snapshot.SkillBindings.Add(skillBinding);
        snapshot.SnapshotSha256 = AgentProfileDeterminism.ComputeExecutionSnapshotSha256(snapshot);
        return AgentProfileDeterminism.NormalizePublishedSnapshot(snapshot);
    }

    private static ProfileToolPolicy ExplicitPolicy(params string[] toolNames)
    {
        var policy = new ProfileToolPolicy
        {
            Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
        };
        policy.ToolNames.Add(toolNames);
        return policy;
    }

    private static ByteString Digest(byte value) =>
        ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());

    private sealed class RecordingNamespaceQueryPort : IAgentProfileNamespaceQueryPort
    {
        public AgentProfileNamespaceEntrySnapshot? Result { get; init; }
        public List<AgentProfileReference> References { get; } = [];

        public Task<AgentProfileNamespaceEntrySnapshot?> GetOwnedAsync(
            AgentProfileOwnerIdentity owner,
            string owningScopeId,
            string profileSlug,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("The conversation binder must use the typed human reference query.");

        public Task<AgentProfileNamespaceEntrySnapshot?> GetByReferenceAsync(
            AgentProfileReference reference,
            CancellationToken ct = default)
        {
            References.Add(reference.Clone());
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
            ProfileIds.Add(profileId);
            return Task.FromResult(Result?.DeepClone());
        }
    }
}
