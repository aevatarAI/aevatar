using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Aevatar.GAgentService.Projection.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.GAgentService.Tests.Abstractions;

public sealed class AgentProfileContractsTests
{
    private const string SkillGuid = "2d05bf2e-88ee-4f76-9998-728ba2f9db10";

    [Fact]
    public void ApplicationMutationCommands_ShouldCarryTheOnlyIngressProofFields()
    {
        var externalCommands = new[]
        {
            CreateAgentProfileCommand.Descriptor,
            UpdateAgentProfileDraftCommand.Descriptor,
            UpsertAgentProfileSkillBindingCommand.Descriptor,
            RemoveAgentProfileSkillBindingCommand.Descriptor,
            PublishAgentProfileCommand.Descriptor,
        };

        externalCommands
            .Select(static descriptor => descriptor.FindFieldByName("ingress_proof"))
            .Should().OnlyContain(static field =>
                field != null && field.MessageType == AgentProfileIngressProof.Descriptor);
        AgentProfilesReflection.Descriptor.MessageTypes
            .Except(externalCommands)
            .Should().NotContain(static descriptor =>
                descriptor.FindFieldByName("ingress_proof") != null);
    }

    [Fact]
    public void IngressProofIntegrity_ShouldHashTheExactCommandWithOnlyProofCleared()
    {
        var command = new UpdateAgentProfileDraftCommand
        {
            Operation = Operation("op-proof", "cmd-proof", "corr-proof", 0x11),
            Identity = ProfileIdentity(),
            ExpectedAuthorityStateVersion = 7,
            Content = Content("Purpose", "Instructions", ["alpha"]),
            IngressProof = new AgentProfileIngressProof
            {
                KeyId = "key-alpha",
                Signature = ByteString.CopyFrom([0x11, 0x22]),
            },
        };

        var first = AgentProfileIngressProofIntegrity.ComputeCanonicalCommandSha256(command);
        command.IngressProof = new AgentProfileIngressProof
        {
            KeyId = "key-beta",
            Signature = ByteString.CopyFrom([0x33, 0x44]),
        };
        var changedProof = AgentProfileIngressProofIntegrity.ComputeCanonicalCommandSha256(command);
        command.ExpectedAuthorityStateVersion = 8;
        var changedCommand = AgentProfileIngressProofIntegrity.ComputeCanonicalCommandSha256(command);

        changedProof.Should().Equal(first);
        changedCommand.Should().NotEqual(first);
    }

    [Fact]
    public void IngressProofIntegrity_ShouldBuildDomainSeparatedDeterministicSigningMaterial()
    {
        var command = new RemoveAgentProfileSkillBindingCommand
        {
            Operation = Operation("op-proof", "cmd-proof", "corr-proof", 0x22),
            Identity = ProfileIdentity(),
            ExpectedAuthorityStateVersion = 11,
            BindingId = "bind-alpha",
        };

        var first = AgentProfileIngressProofIntegrity.CreateSigningMaterial(
            "profile-actor-alpha",
            command);
        var second = AgentProfileIngressProofIntegrity.CreateSigningMaterial(
            "profile-actor-alpha",
            command.Clone());

        second.Should().Be(first);
        first.Domain.Should().Be("aevatar.agent-profile.ingress-proof.v1");
        first.TargetActorId.Should().Be("profile-actor-alpha");
        first.CommandTypeUrl.Should().Be(Any.Pack(command).TypeUrl);
        first.CanonicalCommandSha256.Should().HaveCount(32);
        AgentProfileIngressProofIntegrity.ComputeSigningMaterialSha256(first)
            .Should().Equal(AgentProfileIngressProofIntegrity.ComputeSigningMaterialSha256(second));
    }

    [Fact]
    public void DurableAndProjectedProfileContracts_ShouldNotCarryIngressProofOrSignature()
    {
        var roots = new[]
        {
            AgentProfileProvisioningStartedEvent.Descriptor,
            AgentProfileProvisioningCompletedEvent.Descriptor,
            AgentProfileProvisioningFailedEvent.Descriptor,
            AgentProfilePublishedSummaryObservedEvent.Descriptor,
            AgentProfileInitializedEvent.Descriptor,
            AgentProfileInitializationRejectedEvent.Descriptor,
            AgentProfileDraftUpdatedEvent.Descriptor,
            AgentProfileSkillBindingUpsertedEvent.Descriptor,
            AgentProfileSkillBindingRemovedEvent.Descriptor,
            AgentProfilePublishedEvent.Descriptor,
            AgentProfilePublishNoChangeEvent.Descriptor,
            AgentProfileMutationNoChangeEvent.Descriptor,
            AgentProfileMutationRejectedEvent.Descriptor,
            AgentProfileNamespaceState.Descriptor,
            AgentProfileState.Descriptor,
            AgentProfileNamespaceCatalogDocument.Descriptor,
            AgentProfileOwnerDocument.Descriptor,
            AgentProfileExecutionDocument.Descriptor,
        };

        ReachableFields(roots)
            .Select(static field => field.Name)
            .Should().NotContain(static name =>
                name.Contains("ingress_proof", StringComparison.Ordinal) ||
                name.Contains("signature", StringComparison.Ordinal) ||
                name.Contains("private_key", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateReference_ShouldAcceptCanonicalHumanReferences()
    {
        AgentProfilePolicies.ValidateReference(new AgentProfileReference
        {
            OwnerHandle = "eanzhao",
            ProfileSlug = "xiaomi-home-assistant",
        }).Should().BeEmpty();

        AgentProfilePolicies.ValidateReference(new AgentProfileReference
        {
            OwnerHandle = "system",
            ProfileSlug = "studio",
        }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Eanzhao", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("ean--zhao", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData(".", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("..", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("eanzhao/dev", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("eanzhao\n", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "profile", "INVALID_OWNER_HANDLE")]
    [InlineData("eanzhao", "Profile", "INVALID_PROFILE_SLUG")]
    [InlineData("eanzhao", "home--assistant", "INVALID_PROFILE_SLUG")]
    [InlineData("eanzhao", ".", "INVALID_PROFILE_SLUG")]
    [InlineData("eanzhao", "..", "INVALID_PROFILE_SLUG")]
    [InlineData("eanzhao", "home/assistant", "INVALID_PROFILE_SLUG")]
    [InlineData("eanzhao", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "INVALID_PROFILE_SLUG")]
    public void ValidateReference_ShouldRejectNonCanonicalSegments(
        string ownerHandle,
        string profileSlug,
        string expectedCode)
    {
        var diagnostics = AgentProfilePolicies.ValidateReference(new AgentProfileReference
        {
            OwnerHandle = ownerHandle,
            ProfileSlug = profileSlug,
        });

        diagnostics.Should().ContainSingle(x => x.Code == expectedCode);
    }

    [Fact]
    public void ValidateUserOwnerHandle_ShouldRejectReservedSystemClaim()
    {
        AgentProfilePolicies.ValidateUserOwnerHandle("system")
            .Should().ContainSingle(x => x.Code == "RESERVED_OWNER_HANDLE");
    }

    [Fact]
    public void ValidateIdentity_ShouldRejectReservedPlatformScopeForUserOwner()
    {
        var identity = ProfileIdentity();
        identity.OwningScopeId = PlatformScopeSemantics.ReservedPlatformScopeId;

        AgentProfilePolicies.ValidateIdentity(identity)
            .Should().ContainSingle(diagnostic =>
                diagnostic.Code == "RESERVED_OWNING_SCOPE_ID" &&
                diagnostic.Path == "owning_scope_id");
    }

    [Fact]
    public void CommittedMutationContracts_ShouldShareTypedBeforeAfterTransitionFacts()
    {
        var outcomeTransition = AgentProfileMutationOutcome.Descriptor.FindFieldByName("transition");
        outcomeTransition.Should().NotBeNull();
        outcomeTransition!.FieldType.Should().Be(FieldType.Message);
        outcomeTransition.MessageType.Name.Should().Be("AgentProfileCommittedStateTransition");

        var before = outcomeTransition.MessageType.FindFieldByName("before");
        var after = outcomeTransition.MessageType.FindFieldByName("after");
        before.Should().NotBeNull();
        after.Should().NotBeNull();
        before!.MessageType.Should().BeSameAs(after!.MessageType);
        before.MessageType.Name.Should().Be("AgentProfileRevisionDigestFacts");
        before.MessageType.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .Should()
            .Equal(
                "draft_revision",
                "draft_sha256",
                "published_revision",
                "published_snapshot_sha256");

        AgentProfileInitializedEvent.Descriptor.FindFieldByName("transition")
            .Should().NotBeNull()
            .And.Match<FieldDescriptor>(field => field.MessageType == outcomeTransition.MessageType);
    }

    [Theory]
    [InlineData("1.4")]
    [InlineData("0.0")]
    [InlineData("10.27")]
    public void ValidateExactSkillReference_ShouldAcceptLiteralMajorMinorVersions(string literalVersion)
    {
        AgentProfilePolicies.ValidateExactSkillReference(ExactReference(literalVersion))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("v1.4")]
    [InlineData("1")]
    [InlineData("1.4.0")]
    [InlineData("1.x")]
    [InlineData("01.4")]
    [InlineData("1.4\n")]
    public void ValidateExactSkillReference_ShouldRejectNonLiteralVersions(string literalVersion)
    {
        AgentProfilePolicies.ValidateExactSkillReference(new ExactOrnnSkillReference
        {
            SkillGuid = SkillGuid,
            LiteralVersion = literalVersion,
            ExpectedName = "xiaomi-home-control",
            ExpectedPublisherId = "publisher-alpha",
        }).Should().ContainSingle(x => x.Code == "INVALID_LITERAL_VERSION");
    }

    [Fact]
    public void ComputeDraftSha256_ShouldCanonicalizeToolOrderingAndLineEndings()
    {
        var first = Content(
            purpose: "Control devices.\r\nUse exact state.",
            instructions: "First line.\r\nSecond line.",
            toolNames: ["zeta", "alpha", "alpha"]);
        var second = Content(
            purpose: "Control devices.\nUse exact state.",
            instructions: "First line.\nSecond line.",
            toolNames: ["alpha", "zeta"]);

        AgentProfileDeterminism.ComputeDraftSha256(first)
            .Should().Equal(AgentProfileDeterminism.ComputeDraftSha256(second));
    }

    [Fact]
    public void NormalizeContent_ShouldRejectBindingIdsThatCollideAfterNfcNormalization()
    {
        var bindingIdOrders = new[]
        {
            new[] { "binding-\u00e9", "binding-e\u0301" },
            new[] { "binding-e\u0301", "binding-\u00e9" },
        };

        foreach (var bindingIds in bindingIdOrders)
        {
            var content = Content("Purpose", "Instructions", ["alpha"]);
            content.SkillBindings.Add(bindingIds.Select(static bindingId =>
                new AgentProfileSkillBinding
                {
                    BindingId = bindingId,
                    ActivationMode = AgentProfileSkillActivationMode.Routed,
                    Skill = ExactReference(),
                }));

            AgentProfilePolicies.ValidateContent(content)
                .Should().ContainSingle(x => x.Code == "DUPLICATE_BINDING_ID");
            var act = () => AgentProfileDeterminism.NormalizeContent(content);
            act.Should().Throw<AgentProfileContractValidationException>()
                .Which.Diagnostics.Should().ContainSingle(x => x.Code == "DUPLICATE_BINDING_ID");
        }
    }

    [Fact]
    public void NormalizePublishedSnapshot_ShouldRejectBindingIdsThatCollideAfterNfcNormalization()
    {
        foreach (var reverse in new[] { false, true })
        {
            var snapshot = PublishedSnapshot(Content("Purpose", "Instructions", ["alpha"]));
            var bindings = new[]
            {
                new SealedAgentProfileSkillBinding
                {
                    BindingId = "binding-\u00e9",
                    ActivationMode = AgentProfileSkillActivationMode.Always,
                    Skill = ValidSealedSkill(),
                },
                new SealedAgentProfileSkillBinding
                {
                    BindingId = "binding-e\u0301",
                    ActivationMode = AgentProfileSkillActivationMode.Routed,
                    Skill = ValidSealedSkill(),
                },
            };
            if (reverse)
                Array.Reverse(bindings);
            snapshot.SkillBindings.Add(bindings);

            AgentProfilePolicies.ValidatePublishedSnapshot(snapshot)
                .Should().ContainSingle(x => x.Code == "DUPLICATE_BINDING_ID");
            var act = () => AgentProfileDeterminism.NormalizePublishedSnapshot(snapshot);
            act.Should().Throw<AgentProfileContractValidationException>()
                .Which.Diagnostics.Should().ContainSingle(x => x.Code == "DUPLICATE_BINDING_ID");
        }
    }

    [Fact]
    public void ComputeSealedSkillSha256_ShouldCanonicalizeAssetOrderingAndLineEndings()
    {
        var first = SealedSkill(
            "publisher-alpha",
            [
                new AgentProfileNamedTextAsset { Path = "zeta.txt", Content = "Zeta\r\nline" },
                new AgentProfileNamedTextAsset { Path = "alpha.txt", Content = "Alpha\r\nline" },
            ]);
        var second = SealedSkill(
            "publisher-alpha",
            [
                new AgentProfileNamedTextAsset { Path = "alpha.txt", Content = "Alpha\nline" },
                new AgentProfileNamedTextAsset { Path = "zeta.txt", Content = "Zeta\nline" },
            ]);

        AgentProfileDeterminism.ComputeSealedSkillSha256(first)
            .Should().Equal(AgentProfileDeterminism.ComputeSealedSkillSha256(second));
    }

    [Fact]
    public void NormalizeResolvedSkillPackage_ShouldDeduplicateAndSortIdenticalNormalizedIdentityEntries()
    {
        var package = SealedSkill("publisher-alpha", []).Package;
        package.Workflows.Add([
            Workflow("workflow-\u00e9", "zeta\r\nline", "alpha"),
            Workflow("workflow-e\u0301", "alpha", "zeta\nline"),
            Workflow("workflow-alpha", "alpha"),
        ]);
        package.Scripts.Add([
            Script("script-\u00e9", "Console.WriteLine(\"alpha\");\r\n"),
            Script("script-e\u0301", "Console.WriteLine(\"alpha\");\n"),
            Script("script-alpha", "return;"),
        ]);
        package.Assets.Add([
            new AgentProfileNamedTextAsset { Path = "docs/\u00e9.txt", Content = "alpha\r\nline" },
            new AgentProfileNamedTextAsset { Path = "docs/e\u0301.txt", Content = "alpha\nline" },
            new AgentProfileNamedTextAsset { Path = "docs/alpha.txt", Content = "alpha" },
        ]);

        var normalized = AgentProfileDeterminism.NormalizeResolvedSkillPackage(package);

        normalized.Workflows.Select(static workflow => workflow.WorkflowId)
            .Should().Equal("workflow-alpha", "workflow-\u00e9");
        normalized.Scripts.Select(static script => script.ScriptId)
            .Should().Equal("script-alpha", "script-\u00e9");
        normalized.Assets.Select(static asset => asset.Path)
            .Should().Equal("docs/alpha.txt", "docs/\u00e9.txt");
    }

    [Fact]
    public void NormalizeResolvedSkillPackage_ShouldRejectConflictingNormalizedWorkflowIds()
    {
        var package = SealedSkill("publisher-alpha", []).Package;
        package.Workflows.Add([
            Workflow("workflow-\u00e9", "alpha"),
            Workflow("workflow-e\u0301", "beta"),
        ]);

        var act = () => AgentProfileDeterminism.NormalizeResolvedSkillPackage(package);

        act.Should().Throw<AgentProfileContractValidationException>()
            .Which.Diagnostics.Should().ContainSingle(x => x.Code == "CONFLICTING_WORKFLOW_ID");
    }

    [Fact]
    public void NormalizeResolvedSkillPackage_ShouldRejectConflictingNormalizedScriptIds()
    {
        var package = SealedSkill("publisher-alpha", []).Package;
        package.Scripts.Add([
            Script("script-\u00e9", "return 1;"),
            Script("script-e\u0301", "return 2;"),
        ]);

        var act = () => AgentProfileDeterminism.NormalizeResolvedSkillPackage(package);

        act.Should().Throw<AgentProfileContractValidationException>()
            .Which.Diagnostics.Should().ContainSingle(x => x.Code == "CONFLICTING_SCRIPT_ID");
    }

    [Fact]
    public void NormalizeResolvedSkillPackage_ShouldRejectConflictingNormalizedAssetPaths()
    {
        var package = SealedSkill("publisher-alpha", [
            new AgentProfileNamedTextAsset { Path = "docs/\u00e9.txt", Content = "alpha" },
            new AgentProfileNamedTextAsset { Path = "docs/e\u0301.txt", Content = "beta" },
        ]).Package;

        var act = () => AgentProfileDeterminism.NormalizeResolvedSkillPackage(package);

        act.Should().Throw<AgentProfileContractValidationException>()
            .Which.Diagnostics.Should().ContainSingle(x => x.Code == "CONFLICTING_ASSET_PATH");
    }

    [Fact]
    public void ComputeSealedSkillSha256_ShouldBindExpectedPublisherIdentity()
    {
        var first = SealedSkill("publisher-alpha", []);
        var second = SealedSkill("publisher-beta", []);

        AgentProfileDeterminism.ComputeSealedSkillSha256(first)
            .Should().NotEqual(AgentProfileDeterminism.ComputeSealedSkillSha256(second));
    }

    [Fact]
    public void ValidateSealedSkill_ShouldAcceptMatchingIdentityAndDigest()
    {
        AgentProfilePolicies.ValidateSealedSkill(ValidSealedSkill())
            .Should().BeEmpty();
    }

    [Fact]
    public void ValidateSealedSkill_ShouldRejectReferencePackageIdentityMismatches()
    {
        var cases = new (Action<SealedAgentProfileSkill> Mutate, string Code)[]
        {
            (skill => skill.Package.SkillGuid = "3d05bf2e-88ee-4f76-9998-728ba2f9db10",
                "SEALED_SKILL_GUID_MISMATCH"),
            (skill => skill.Package.LiteralVersion = "1.5",
                "SEALED_SKILL_LITERAL_VERSION_MISMATCH"),
            (skill => skill.Package.CanonicalName = "another-skill",
                "SEALED_SKILL_CANONICAL_NAME_MISMATCH"),
            (skill => skill.Package.PublisherId = "publisher-beta",
                "SEALED_SKILL_PUBLISHER_ID_MISMATCH"),
        };

        foreach (var (mutate, code) in cases)
        {
            var skill = ValidSealedSkill();
            mutate(skill);

            AgentProfilePolicies.ValidateSealedSkill(skill)
                .Should().Contain(diagnostic => diagnostic.Code == code);
        }
    }

    [Fact]
    public void ValidateSealedSkill_ShouldRejectTamperedContentDigest()
    {
        var skill = ValidSealedSkill();
        skill.Package.Instructions = "Tampered instructions";

        AgentProfilePolicies.ValidateSealedSkill(skill)
            .Should().Contain(diagnostic =>
                diagnostic.Code == "SEALED_SKILL_CONTENT_SHA256_MISMATCH");
    }

    [Fact]
    public void ValidatePublishedSnapshotHardLimits_ShouldBoundEveryDiagnosticFieldByUtf8Bytes()
    {
        var snapshot = PublishedSnapshot(Content(
            purpose: "Control devices.",
            instructions: "Follow the Profile procedure.",
            toolNames: []));
        var skill = ValidSealedSkill();
        skill.Package.Assets.Add(new AgentProfileNamedTextAsset
        {
            Path = new string('\u00e9', 600),
            Content = new string('a', AgentProfileValidationLimits.TextAssetMaxUtf8Bytes + 1),
        });
        snapshot.SkillBindings.Add(new SealedAgentProfileSkillBinding
        {
            BindingId = "bind-alpha",
            ActivationMode = AgentProfileSkillActivationMode.Routed,
            Skill = skill,
        });

        var diagnostic = AgentProfilePolicies.ValidatePublishedSnapshotHardLimits(snapshot)
            .Single(candidate => candidate.Code == "TEXT_ASSET_TOO_LARGE");

        Encoding.UTF8.GetByteCount(diagnostic.Code).Should().BeLessThanOrEqualTo(512);
        Encoding.UTF8.GetByteCount(diagnostic.Message).Should().BeLessThanOrEqualTo(512);
        Encoding.UTF8.GetByteCount(diagnostic.Path).Should().BeLessThanOrEqualTo(512);
    }

    [Fact]
    public void Purpose_ShouldAffectDraftDigestButNotExecutionSnapshotDigest()
    {
        var firstContent = Content("First purpose", "Instructions", ["alpha"]);
        var secondContent = Content("Second purpose", "Instructions", ["alpha"]);
        var firstSnapshot = PublishedSnapshot(firstContent);
        var secondSnapshot = PublishedSnapshot(secondContent);

        AgentProfileDeterminism.ComputeDraftSha256(firstContent)
            .Should().NotEqual(AgentProfileDeterminism.ComputeDraftSha256(secondContent));
        AgentProfileDeterminism.ComputePublishedSnapshotSha256(firstSnapshot)
            .Should().Equal(AgentProfileDeterminism.ComputePublishedSnapshotSha256(secondSnapshot));
    }

    [Fact]
    public void OperationInputSha256_ShouldNormalizeSemanticPayloadInternally()
    {
        var target = ProfileIdentity();
        var first = Content(
            purpose: "Control devices.\r\nUse exact state.",
            instructions: "First line.\r\nSecond line.",
            toolNames: ["zeta", "alpha", "alpha"]);
        var second = Content(
            purpose: "Control devices.\nUse exact state.",
            instructions: "First line.\nSecond line.",
            toolNames: ["alpha", "zeta"]);

        AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(target, first)
            .Should().Equal(
                AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(target, second));
    }

    [Fact]
    public void OperationInputSha256_ShouldSeparateTargetAndOperationMessageType()
    {
        var content = Content("Purpose", "Instructions", ["alpha"]);
        var target = ProfileIdentity("prof-alpha");
        var otherTarget = ProfileIdentity("prof-beta");

        var update = AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(
            target,
            content);

        update.Should().NotEqual(
            AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(otherTarget, content));
        update.Should().NotEqual(
            AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(target, content));
    }

    [Fact]
    public void OperationInputSha256_ShouldExcludeConcurrencyAndTransportFacts()
    {
        var target = ProfileIdentity();
        var content = Content("Purpose", "Instructions", ["alpha"]);
        var first = new UpdateAgentProfileDraftCommand
        {
            Identity = target.Clone(),
            Content = content.Clone(),
            ExpectedAuthorityStateVersion = 7,
            Operation = Operation("op-alpha", "cmd-alpha", "corr-alpha", 0x11),
        };
        var second = new UpdateAgentProfileDraftCommand
        {
            Identity = target.Clone(),
            Content = content.Clone(),
            ExpectedAuthorityStateVersion = 99,
            Operation = Operation("op-beta", "cmd-beta", "corr-beta", 0x22),
        };

        AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(
                first.Identity,
                first.Content)
            .Should().Equal(
                AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(
                    second.Identity,
                    second.Content));
    }

    [Fact]
    public void OperationInputSha256_ShouldExposeOnlyOperationSpecificTypedMethods()
    {
        typeof(AgentProfileDeterminism).GetMethod("ComputeInputSha256")
            .Should().BeNull();

        var signatures = typeof(AgentProfileDeterminism)
            .GetMethods()
            .Where(static method => method.Name.EndsWith("InputSha256", StringComparison.Ordinal))
            .ToDictionary(
                static method => method.Name,
                static method => method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());

        signatures.Should().BeEquivalentTo(new Dictionary<string, Type[]>
        {
            ["ComputeCreateAgentProfileInputSha256"] =
                [typeof(AgentProfileIdentity), typeof(AgentProfileContent)],
            ["ComputeUpdateAgentProfileDraftInputSha256"] =
                [typeof(AgentProfileIdentity), typeof(AgentProfileContent)],
            ["ComputeUpsertAgentProfileSkillBindingInputSha256"] =
                [typeof(AgentProfileIdentity), typeof(AgentProfileSkillBinding)],
            ["ComputeRemoveAgentProfileSkillBindingInputSha256"] =
                [typeof(AgentProfileIdentity), typeof(string)],
            ["ComputePublishAgentProfileInputSha256"] =
                [typeof(AgentProfileIdentity), typeof(AgentProfilePublishedSnapshot)],
        });
    }

    [Fact]
    public void CreateIds_ShouldBeStableForOwnerScopeAndIdempotencyKey()
    {
        var owner = UserOwner("subject-alpha");

        var operationId = AgentProfileDeterminism.CreateOperationId(owner, "scope-gamma", "key-alpha");
        var profileId = AgentProfileDeterminism.CreateProfileId(owner, "scope-gamma", "key-alpha");

        AgentProfileDeterminism.CreateOperationId(owner, "scope-gamma", "key-alpha")
            .Should().Be(operationId);
        AgentProfileDeterminism.CreateProfileId(owner, "scope-gamma", "key-alpha")
            .Should().Be(profileId);
    }

    [Fact]
    public void CreateIds_ShouldChangeWhenOwnerOrIdempotencyKeyChanges()
    {
        var owner = UserOwner("subject-alpha");
        var otherOwner = UserOwner("subject-beta");
        var operationId = AgentProfileDeterminism.CreateOperationId(owner, "scope-gamma", "key-alpha");
        var profileId = AgentProfileDeterminism.CreateProfileId(owner, "scope-gamma", "key-alpha");

        AgentProfileDeterminism.CreateOperationId(otherOwner, "scope-gamma", "key-alpha")
            .Should().NotBe(operationId);
        AgentProfileDeterminism.CreateProfileId(otherOwner, "scope-gamma", "key-alpha")
            .Should().NotBe(profileId);
        AgentProfileDeterminism.CreateOperationId(owner, "scope-gamma", "key-beta")
            .Should().NotBe(operationId);
        AgentProfileDeterminism.CreateProfileId(owner, "scope-gamma", "key-beta")
            .Should().NotBe(profileId);
    }

    [Fact]
    public void CreateCommandId_ShouldReturnANewDispatchIdentity()
    {
        AgentProfileDeterminism.CreateCommandId()
            .Should().NotBe(AgentProfileDeterminism.CreateCommandId());
    }

    [Fact]
    public void FailedResolution_ShouldBoundSafeDiagnosticMessage()
    {
        var result = ExactOrnnSkillResolutionResult.Failed(
            "ORNN_DEPENDENCY_UNAVAILABLE",
            new string('x', 600));

        Encoding.UTF8.GetByteCount(result.Failure!.Message).Should().BeLessThanOrEqualTo(512);
    }

    [Fact]
    public void ProtobufBearingValueRecords_ShouldCloneConstructorInputs()
    {
        var owner = UserOwner("subject-alpha");
        var policy = new AgentProfileToolPolicy { Mode = AgentProfileToolPolicyMode.ExplicitAllowlist };
        policy.ToolNames.Add("alpha");
        var exactReference = ExactReference();
        var diagnostic = new AgentProfileSafeDiagnostic { Code = "VALIDATION_ALPHA" };
        var diagnostics = new List<AgentProfileSafeDiagnostic> { diagnostic };
        var resolution = new AgentProfileSkillResolutionSummary(
            "bind-beta",
            exactReference,
            ByteString.CopyFrom([0x11]));
        var resolutions = new List<AgentProfileSkillResolutionSummary> { resolution };
        var identity = ProfileIdentity();
        var draft = Content("Purpose", "Instructions", ["alpha"]);
        var mutation = new AgentProfileMutationOutcome
        {
            Operation = Operation("op-alpha", "cmd-alpha", "corr-alpha", 0x11),
        };
        var reference = identity.Reference.Clone();
        var publishedSummary = new AgentProfilePublishedSummary
        {
            Reference = reference.Clone(),
            DisplayName = "Home assistant",
        };
        var published = PublishedSnapshot(draft);

        var caller = new AgentProfileCallerContext(owner, "scope-gamma", "eanzhao", "token-alpha");
        var create = new CreateAgentProfileRequest(
            "xiaomi-home-assistant", null, "Home assistant", "Purpose", "Instructions", policy);
        var update = new UpdateAgentProfileDraftRequest(
            "Home assistant", "Purpose", "Instructions", policy);
        var upsert = new UpsertAgentProfileSkillBindingRequest(
            AgentProfileSkillActivationMode.Routed, exactReference);
        var report = new AgentProfileValidationReport(
            true, 3, ByteString.CopyFrom([0x22]), diagnostics, resolutions);
        var namespaceEntry = new AgentProfileNamespaceEntrySnapshot(
            4, "evt-4", "prof-alpha", reference, identity.Owner, "scope-gamma",
            AgentProfileProvisioningStatus.Active, publishedSummary);
        var management = new AgentProfileManagementSnapshot(
            5, "evt-5", identity, draft, 3, ByteString.CopyFrom([0x33]),
            2, ByteString.CopyFrom([0x44]), ByteString.CopyFrom([0x55]), mutation);
        var execution = new AgentProfileExecutionSnapshot(6, "evt-6", published);
        var discovery = new AgentProfileDiscoverySnapshot(
            reference, "Home assistant", "Purpose", 2, true);
        var exception = new AgentProfileContractValidationException(diagnostics);

        owner.SubjectId = "mutated-owner";
        policy.ToolNames[0] = "mutated-tool";
        exactReference.ExpectedName = "mutated-skill";
        diagnostic.Code = "MUTATED_DIAGNOSTIC";
        diagnostics.Clear();
        resolutions.Clear();
        identity.ProfileId = "mutated-profile";
        draft.DisplayName = "Mutated draft";
        mutation.Operation.CommandId = "mutated-command";
        reference.ProfileSlug = "mutated-slug";
        publishedSummary.DisplayName = "Mutated summary";
        published.DisplayName = "Mutated published";

        caller.Owner.SubjectId.Should().Be("subject-alpha");
        create.ToolPolicy.ToolNames.Should().Equal("alpha");
        update.ToolPolicy.ToolNames.Should().Equal("alpha");
        upsert.Skill.ExpectedName.Should().Be("xiaomi-home-control");
        resolution.ExactReference.ExpectedName.Should().Be("xiaomi-home-control");
        report.Diagnostics.Should().ContainSingle(x => x.Code == "VALIDATION_ALPHA");
        report.ResolvedSkills.Should().ContainSingle();
        namespaceEntry.Reference.ProfileSlug.Should().Be("xiaomi-home-assistant");
        namespaceEntry.Owner.User.SubjectId.Should().Be("subject-alpha");
        namespaceEntry.PublishedSummary!.DisplayName.Should().Be("Home assistant");
        management.Identity.ProfileId.Should().Be("prof-alpha");
        management.Draft.DisplayName.Should().Be("Home assistant");
        management.LastMutation!.Operation.CommandId.Should().Be("cmd-alpha");
        execution.Snapshot.DisplayName.Should().Be("Home assistant");
        discovery.Reference.ProfileSlug.Should().Be("xiaomi-home-assistant");
        exception.Diagnostics.Should().ContainSingle(x => x.Code == "VALIDATION_ALPHA");
    }

    [Fact]
    public void ProtobufBearingValueRecords_ShouldCloneValuesOnEveryAccess()
    {
        var policy = new AgentProfileToolPolicy { Mode = AgentProfileToolPolicyMode.ExplicitAllowlist };
        policy.ToolNames.Add("alpha");
        var exactReference = ExactReference();
        var diagnostic = new AgentProfileSafeDiagnostic { Code = "VALIDATION_ALPHA" };
        var resolution = new AgentProfileSkillResolutionSummary(
            "bind-beta", exactReference, ByteString.CopyFrom([0x11]));
        var identity = ProfileIdentity();
        var draft = Content("Purpose", "Instructions", ["alpha"]);
        var mutation = new AgentProfileMutationOutcome
        {
            Operation = Operation("op-alpha", "cmd-alpha", "corr-alpha", 0x11),
        };
        var summary = new AgentProfilePublishedSummary
        {
            Reference = identity.Reference.Clone(),
            DisplayName = "Home assistant",
        };

        var caller = new AgentProfileCallerContext(
            UserOwner("subject-alpha"), "scope-gamma", "eanzhao", "token-alpha");
        var create = new CreateAgentProfileRequest(
            "xiaomi-home-assistant", null, "Home assistant", "Purpose", "Instructions", policy);
        var update = new UpdateAgentProfileDraftRequest(
            "Home assistant", "Purpose", "Instructions", policy);
        var upsert = new UpsertAgentProfileSkillBindingRequest(
            AgentProfileSkillActivationMode.Routed, exactReference);
        var report = new AgentProfileValidationReport(
            true, 3, ByteString.CopyFrom([0x22]), [diagnostic], [resolution]);
        var namespaceEntry = new AgentProfileNamespaceEntrySnapshot(
            4, "evt-4", "prof-alpha", identity.Reference, identity.Owner, "scope-gamma",
            AgentProfileProvisioningStatus.Active, summary);
        var management = new AgentProfileManagementSnapshot(
            5, "evt-5", identity, draft, 3, ByteString.CopyFrom([0x33]),
            2, ByteString.CopyFrom([0x44]), ByteString.CopyFrom([0x55]), mutation);
        var execution = new AgentProfileExecutionSnapshot(6, "evt-6", PublishedSnapshot(draft));
        var discovery = new AgentProfileDiscoverySnapshot(
            identity.Reference, "Home assistant", "Purpose", 2, true);
        var exception = new AgentProfileContractValidationException([diagnostic]);

        caller.Owner.SubjectId = "returned-owner";
        create.ToolPolicy.ToolNames[0] = "returned-create-tool";
        update.ToolPolicy.ToolNames[0] = "returned-update-tool";
        upsert.Skill.ExpectedName = "returned-upsert-skill";
        resolution.ExactReference.ExpectedName = "returned-resolution-skill";
        report.Diagnostics[0].Code = "RETURNED_DIAGNOSTIC";
        report.ResolvedSkills[0].ExactReference.ExpectedName = "returned-report-skill";
        namespaceEntry.Reference.ProfileSlug = "returned-namespace-slug";
        namespaceEntry.Owner.User.SubjectId = "returned-namespace-owner";
        namespaceEntry.PublishedSummary!.DisplayName = "Returned summary";
        management.Identity.ProfileId = "returned-profile";
        management.Draft.DisplayName = "Returned draft";
        management.LastMutation!.Operation.CommandId = "returned-command";
        execution.Snapshot.DisplayName = "Returned published";
        discovery.Reference.ProfileSlug = "returned-discovery-slug";
        exception.Diagnostics[0].Code = "RETURNED_EXCEPTION";

        caller.Owner.SubjectId.Should().Be("subject-alpha");
        create.ToolPolicy.ToolNames.Should().Equal("alpha");
        update.ToolPolicy.ToolNames.Should().Equal("alpha");
        upsert.Skill.ExpectedName.Should().Be("xiaomi-home-control");
        resolution.ExactReference.ExpectedName.Should().Be("xiaomi-home-control");
        report.Diagnostics.Should().ContainSingle(x => x.Code == "VALIDATION_ALPHA");
        report.ResolvedSkills[0].ExactReference.ExpectedName.Should().Be("xiaomi-home-control");
        namespaceEntry.Reference.ProfileSlug.Should().Be("xiaomi-home-assistant");
        namespaceEntry.Owner.User.SubjectId.Should().Be("subject-alpha");
        namespaceEntry.PublishedSummary!.DisplayName.Should().Be("Home assistant");
        management.Identity.ProfileId.Should().Be("prof-alpha");
        management.Draft.DisplayName.Should().Be("Home assistant");
        management.LastMutation!.Operation.CommandId.Should().Be("cmd-alpha");
        execution.Snapshot.DisplayName.Should().Be("Home assistant");
        discovery.Reference.ProfileSlug.Should().Be("xiaomi-home-assistant");
        exception.Diagnostics.Should().ContainSingle(x => x.Code == "VALIDATION_ALPHA");
    }

    [Fact]
    public void SealedAndPublishedMessages_ShouldExposeNoCredentialOrGenericBagFields()
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Metadata",
            "Headers",
            "Items",
            "AccessToken",
            "Bearer",
            "ApiKey",
            "Cookie",
            "Credential",
        };

        var fieldNames = ReachableFields(
                SealedAgentProfileSkill.Descriptor,
                AgentProfilePublishedSnapshot.Descriptor)
            .Select(static field => field.PropertyName)
            .ToArray();

        fieldNames.Should().NotContain(name => forbidden.Contains(name));
    }

    private static ExactOrnnSkillReference ExactReference(string literalVersion = "1.4") => new()
    {
        SkillGuid = SkillGuid,
        LiteralVersion = literalVersion,
        ExpectedName = "xiaomi-home-control",
        ExpectedPublisherId = "publisher-alpha",
    };

    private static AgentProfileUserOwnerIdentity UserOwner(string subjectId) => new()
    {
        IdentityProvider = "nyxid",
        SubjectId = subjectId,
    };

    private static AgentProfileIdentity ProfileIdentity(string profileId = "prof-alpha") => new()
    {
        ProfileId = profileId,
        Owner = new AgentProfileOwnerIdentity
        {
            User = UserOwner("subject-alpha"),
        },
        OwningScopeId = "scope-gamma",
        Reference = new AgentProfileReference
        {
            OwnerHandle = "eanzhao",
            ProfileSlug = "xiaomi-home-assistant",
        },
    };

    private static AgentProfileOperationFact Operation(
        string operationId,
        string commandId,
        string correlationId,
        byte digestByte) =>
        new()
        {
            OperationId = operationId,
            CommandId = commandId,
            CorrelationId = correlationId,
            InputSha256 = ByteString.CopyFrom(Enumerable.Repeat(digestByte, 32).ToArray()),
        };

    private static AgentProfileContent Content(
        string purpose,
        string instructions,
        IEnumerable<string> toolNames)
    {
        var content = new AgentProfileContent
        {
            DisplayName = "Home assistant",
            Purpose = purpose,
            Instructions = instructions,
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
            },
        };
        content.ToolPolicy.ToolNames.Add(toolNames);
        return content;
    }

    private static AgentProfileWorkflowAsset Workflow(
        string workflowId,
        params string[] workflowYamls)
    {
        var workflow = new AgentProfileWorkflowAsset { WorkflowId = workflowId };
        workflow.WorkflowYamls.Add(workflowYamls);
        return workflow;
    }

    private static AgentProfileScriptAsset Script(string scriptId, string source)
    {
        var script = new AgentProfileScriptAsset
        {
            ScriptId = scriptId,
            EntryBehaviorTypeName = "Example.EntryBehavior",
        };
        script.SourceFiles.Add(new AgentProfileNamedTextAsset
        {
            Path = "main.cs",
            Content = source,
        });
        return script;
    }

    private static SealedAgentProfileSkill SealedSkill(
        string expectedPublisherId,
        IEnumerable<AgentProfileNamedTextAsset> assets)
    {
        var skill = new SealedAgentProfileSkill
        {
            ExactReference = ExactReference(),
            Package = new ResolvedOrnnSkillPackage
            {
                SkillGuid = SkillGuid,
                LiteralVersion = "1.4",
                CanonicalName = "xiaomi-home-control",
                PublisherId = expectedPublisherId,
                UpstreamSkillHash = "hash-alpha",
                Description = "Controls a home",
                Instructions = "Inspect state.\nApply requested change.",
                Arguments = "device and action",
                WhenToUse = "Use for home control",
                ModelInvocable = true,
                UserInvocable = true,
            },
        };
        skill.ExactReference.ExpectedPublisherId = expectedPublisherId;
        skill.Package.DeclaredToolNames.Add(["zeta", "alpha"]);
        skill.Package.Assets.Add(assets);
        return skill;
    }

    private static SealedAgentProfileSkill ValidSealedSkill()
    {
        var skill = SealedSkill("publisher-alpha", []);
        skill.ContentSha256 = AgentProfileDeterminism.ComputeSealedSkillSha256(skill);
        return skill;
    }

    private static AgentProfilePublishedSnapshot PublishedSnapshot(AgentProfileContent content)
    {
        var snapshot = new AgentProfilePublishedSnapshot
        {
            Identity = ProfileIdentity(),
            DisplayName = content.DisplayName,
            Purpose = content.Purpose,
            Instructions = content.Instructions,
            ToolPolicy = content.ToolPolicy.Clone(),
            PublishedRevision = 7,
        };
        return snapshot;
    }

    private static IEnumerable<FieldDescriptor> ReachableFields(params MessageDescriptor[] roots)
    {
        var pending = new Stack<MessageDescriptor>(roots);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (pending.TryPop(out var message))
        {
            if (!visited.Add(message.FullName))
                continue;

            foreach (var field in message.Fields.InFieldNumberOrder())
            {
                yield return field;
                if (field.FieldType == FieldType.Message && field.MessageType != null)
                    pending.Push(field.MessageType);
            }
        }
    }
}
