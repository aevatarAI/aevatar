using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using ProfileValidationLimits =
    Aevatar.GAgentService.Abstractions.AgentProfiles.AgentProfileValidationLimits;

namespace Aevatar.AI.Tests;

public sealed class AgentProfileExecutionBindingCodecTests
{
    [Fact]
    public void RuntimeProfileAuthority_ShouldUseExecutionBindingWireContract()
    {
        var runtimeMessageNames = AiMessagesReflection.Descriptor.MessageTypes
            .Select(static message => message.Name)
            .ToArray();
        runtimeMessageNames.Should().Contain("AgentProfileExecutionBinding");
        runtimeMessageNames.Should().NotContain([
            "AgentProfileSnapshot",
            "AgentProfileSkillMember",
            "ExactRemoteSkillsetRef",
        ]);
        RoleGAgentState.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Contain("agent_profile_binding");
        NyxIdChatConversationCreateCommand.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Contain("agent_profile_binding");
    }

    [Fact]
    public void SealExecutionBinding_ShouldPersistCompleteProvenanceAndCloneInput()
    {
        var input = BuildExecutionBinding();

        var sealedBinding = AgentProfileExecutionBindingCodec.Seal(input);
        input.Source.StateVersion = 99;
        input.Members[0].InstructionBody = "mutated";

        sealedBinding.Source.ProfileId.Should().Be("profile-alpha");
        sealedBinding.Source.StateVersion.Should().Be(17);
        sealedBinding.Source.PublishedRevision.Should().Be(5);
        sealedBinding.Source.PublishedSnapshotSha256.Should().HaveCount(32);
        sealedBinding.Admission.RolloutRelease.Should().Be("nyxid-chat-r7");
        sealedBinding.Admission.RolloutStage.Should().Be("canary");
        sealedBinding.Admission.RouteToolSetRef.Should().Be("nyxid.profile.route");
        sealedBinding.Admission.AdmissionSha256.Should().HaveCount(32);
        sealedBinding.Members.Should().ContainSingle()
            .Which.InstructionBody.Should().Be("Use the exact sealed procedure.");
        sealedBinding.DeterministicBindingSha256.Should().HaveCount(32);
        AgentProfileExecutionBindingCodec.Verify(sealedBinding).Should().BeTrue();
    }

    [Fact]
    public void VerifyExecutionBinding_ShouldRejectProvenanceOrSealedBodyTampering()
    {
        var sealedBinding = AgentProfileExecutionBindingCodec.Seal(BuildExecutionBinding());
        var provenanceTamper = sealedBinding.Clone();
        provenanceTamper.Source.StateVersion++;
        var admissionTamper = sealedBinding.Clone();
        admissionTamper.Admission.RolloutStage = "production";
        var bodyTamper = sealedBinding.Clone();
        bodyTamper.Members[0].InstructionBody = "different procedure";
        var contentDigestTamper = sealedBinding.Clone();
        contentDigestTamper.Members[0].InstructionBodySha256 = ByteString.CopyFrom(new byte[32]);

        AgentProfileExecutionBindingCodec.Verify(provenanceTamper).Should().BeFalse();
        AgentProfileExecutionBindingCodec.Verify(admissionTamper).Should().BeFalse();
        AgentProfileExecutionBindingCodec.Verify(bodyTamper).Should().BeFalse();
        AgentProfileExecutionBindingCodec.Verify(contentDigestTamper).Should().BeFalse();
    }

    [Fact]
    public void SealExecutionBinding_ShouldIncludeUnknownFieldsInDigestAndByteEquality()
    {
        var baselineBytes = BuildExecutionBinding().ToByteArray();
        var withUnknownA = AgentProfileExecutionBinding.Parser.ParseFrom(
            baselineBytes.Concat(new byte[] { 0xA0, 0x06, 0x01 }).ToArray());
        var withUnknownB = AgentProfileExecutionBinding.Parser.ParseFrom(
            baselineBytes.Concat(new byte[] { 0xA0, 0x06, 0x02 }).ToArray());

        var sealedA = AgentProfileExecutionBindingCodec.Seal(withUnknownA);
        var sealedB = AgentProfileExecutionBindingCodec.Seal(withUnknownB);

        sealedA.DeterministicBindingSha256.Should().NotEqual(sealedB.DeterministicBindingSha256);
        AgentProfileExecutionBindingCodec.Verify(sealedA).Should().BeTrue();
        AgentProfileExecutionBindingCodec.Verify(sealedB).Should().BeTrue();
        AgentProfileExecutionBindingCodec.ByteEquivalent(sealedA, sealedB).Should().BeFalse();
    }

    [Fact]
    public void ExecutionMemberActivation_ShouldUseTypedFrozenWireShape()
    {
        AiMessagesReflection.Descriptor.EnumTypes
            .Single(static descriptor =>
                descriptor.Name == "AgentProfileExecutionMemberActivationMode")
            .Values
            .Select(static value => (value.Name, value.Number))
            .Should()
            .Equal(
                ("AGENT_PROFILE_EXECUTION_MEMBER_ACTIVATION_MODE_UNSPECIFIED", 0),
                ("AGENT_PROFILE_EXECUTION_MEMBER_ACTIVATION_MODE_ROUTED", 1),
                ("AGENT_PROFILE_EXECUTION_MEMBER_ACTIVATION_MODE_DEFAULT_FOR_UNMATCHED_TURN", 2),
                ("AGENT_PROFILE_EXECUTION_MEMBER_ACTIVATION_MODE_ALWAYS", 3));
        AgentProfileExecutionMember.Descriptor.Fields.InFieldNumberOrder()
            .Should().ContainSingle(field =>
                field.Name == "activation_mode" && field.FieldNumber == 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void SealExecutionBinding_ShouldRejectUndefinedMemberActivation(int activationValue)
    {
        var binding = BuildExecutionBinding();
        binding.Members[0].ActivationMode =
            (AgentProfileExecutionMemberActivationMode)activationValue;

        var act = () => AgentProfileExecutionBindingCodec.Seal(binding);

        act.Should().Throw<ArgumentException>().WithMessage("*activation*");
    }

    [Fact]
    public void ExecutionMemberActivation_ShouldParticipateInBindingDigestAndTamperValidation()
    {
        var routed = AgentProfileExecutionBindingCodec.Seal(BuildExecutionBinding());
        var tampered = routed.Clone();
        tampered.Members[0].ActivationMode =
            AgentProfileExecutionMemberActivationMode.DefaultForUnmatchedTurn;
        var defaultBinding = tampered.Clone();
        defaultBinding.DeterministicBindingSha256 = ByteString.Empty;
        defaultBinding = AgentProfileExecutionBindingCodec.Seal(defaultBinding);

        AgentProfileExecutionBindingCodec.Verify(tampered).Should().BeFalse();
        defaultBinding.DeterministicBindingSha256.Should()
            .NotEqual(routed.DeterministicBindingSha256);
        defaultBinding.Members[0].ActivationMode.Should()
            .Be(AgentProfileExecutionMemberActivationMode.DefaultForUnmatchedTurn);
    }

    [Fact]
    public void SealExecutionBinding_ShouldRejectMultipleDefaultMembers()
    {
        var binding = BuildExecutionBinding();
        binding.Members[0].ActivationMode =
            AgentProfileExecutionMemberActivationMode.DefaultForUnmatchedTurn;
        var secondDefault = binding.Members[0].Clone();
        secondDefault.IntentId = "intent-beta";
        secondDefault.ExplicitTriggerAliases.Clear();
        secondDefault.ExplicitTriggerAliases.Add("beta");
        binding.Members.Add(secondDefault);

        var act = () => AgentProfileExecutionBindingCodec.Seal(binding);

        act.Should().Throw<ArgumentException>().WithMessage("*default*");
    }

    [Fact]
    public void SealExecutionBinding_ShouldAcceptAlwaysMemberWithoutRoutingFields()
    {
        var binding = BuildExecutionBinding();
        ConfigureAlwaysMember(binding.Members[0], "Use this procedure on every turn.");

        var sealedBinding = AgentProfileExecutionBindingCodec.Seal(binding);

        AgentProfileExecutionBindingCodec.Verify(sealedBinding).Should().BeTrue();
        sealedBinding.Members[0].ActivationMode.Should()
            .Be(AgentProfileExecutionMemberActivationMode.Always);
        sealedBinding.Members[0].IntentId.Should().BeEmpty();
        sealedBinding.Members[0].RoutingDescription.Should().BeEmpty();
        sealedBinding.Members[0].ExplicitTriggerAliases.Should().BeEmpty();
        sealedBinding.Members[0].TaskToolPolicy.Should().BeNull();
        sealedBinding.Members[0].SideEffectClass.Should()
            .Be(AgentProfileSideEffectClass.Unspecified);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("description")]
    [InlineData("alias")]
    [InlineData("task-policy")]
    [InlineData("side-effect")]
    public void SealExecutionBinding_ShouldRejectRoutingFieldsOnAlwaysMember(string routingField)
    {
        var binding = BuildExecutionBinding();
        var member = binding.Members[0];
        ConfigureAlwaysMember(member, "Use this procedure on every turn.");
        switch (routingField)
        {
            case "intent":
                member.IntentId = "forbidden-intent";
                break;
            case "description":
                member.RoutingDescription = "Forbidden routing description.";
                break;
            case "alias":
                member.ExplicitTriggerAliases.Add("forbidden-alias");
                break;
            case "task-policy":
                member.TaskToolPolicy = new AgentProfileToolPolicy { ToolNames = { "task-tool" } };
                break;
            case "side-effect":
                member.SideEffectClass = AgentProfileSideEffectClass.ServiceCall;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(routingField), routingField, null);
        }

        var act = () => AgentProfileExecutionBindingCodec.Seal(binding);

        act.Should().Throw<ArgumentException>().WithMessage("*always*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SealExecutionBinding_ShouldRequireRoutingFieldsForRoutedAndDefaultMembers(
        int activationMode)
    {
        var binding = BuildExecutionBinding();
        var member = binding.Members[0];
        member.ActivationMode = (AgentProfileExecutionMemberActivationMode)activationMode;
        member.IntentId = string.Empty;
        member.RoutingDescription = string.Empty;
        member.ExplicitTriggerAliases.Clear();
        member.TaskToolPolicy = null;
        member.SideEffectClass = AgentProfileSideEffectClass.Unspecified;

        var act = () => AgentProfileExecutionBindingCodec.Seal(binding);

        act.Should().Throw<ArgumentException>().WithMessage("*routing*");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void SealExecutionBinding_ShouldEnforceRawAuthoritativeAggregateContentLimit(
        int extraBodyBytes,
        bool expectedValid)
    {
        AgentProfileExecutionBindingLimits.RawAuthoritativeAggregateContentMaxUtf8Bytes.Should()
            .Be(ProfileValidationLimits.RawAuthoritativeAggregateContentMaxUtf8Bytes);
        AgentProfileExecutionBindingLimits.RawAuthoritativeAggregateContentMaxEstimatedTokens.Should()
            .Be(ProfileValidationLimits.RawAuthoritativeAggregateContentMaxEstimatedTokens);
        var binding = BuildRawAggregateBinding(extraBodyBytes);

        var act = () => AgentProfileExecutionBindingCodec.Seal(binding);

        if (expectedValid)
        {
            var sealedBinding = act();
            AgentProfileExecutionBindingCodec.Verify(sealedBinding).Should().BeTrue();
        }
        else
        {
            act.Should().Throw<ArgumentException>().WithMessage("*aggregate*profile*prompt*");
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void SealExecutionBinding_ShouldEnforceExactRenderedProfileLayerLimit(
        int extraRenderedByte,
        bool expectedValid)
    {
        var boundary = BuildRenderedProfileLayerBoundary(extraRenderedByte);
        var binding = BuildExecutionBinding();
        binding.ProfileInstructions = boundary.ProfileInstructions;
        ConfigureAlwaysMember(binding.Members[0], boundary.AlwaysProcedures[0]);
        var second = binding.Members[0].Clone();
        second.SkillProvenance.ExactSkillRef.Guid = "22222222-2222-2222-2222-222222222222";
        ConfigureAlwaysMember(second, boundary.AlwaysProcedures[1]);
        binding.Members.Add(second);

        var act = () => AgentProfileExecutionBindingCodec.Seal(binding);

        if (expectedValid)
        {
            act().Should().NotBeNull();
        }
        else
        {
            act.Should().Throw<ArgumentException>().WithMessage("*materialized*profile*layer*");
        }
    }

    [Fact]
    public void SealExecutionBinding_ShouldRejectMismatchedInstructionBodyDigest()
    {
        var binding = BuildExecutionBinding();
        binding.Members[0].InstructionBodySha256 = ByteString.CopyFrom(new byte[32]);

        var act = () => AgentProfileExecutionBindingCodec.Seal(binding);

        act.Should().Throw<ArgumentException>().WithMessage("*instruction body digest*");
    }

    [Fact]
    public void SealExecutionBinding_ShouldRejectAliasesThatCollideAfterRuntimeNormalization()
    {
        var binding = BuildExecutionBinding();
        var collidingMember = binding.Members[0].Clone();
        collidingMember.IntentId = "intent-beta";
        collidingMember.ExplicitTriggerAliases.Clear();
        collidingMember.ExplicitTriggerAliases.Add(" alpha ");
        binding.Members.Add(collidingMember);

        var act = () => AgentProfileExecutionBindingCodec.Seal(binding);

        act.Should().Throw<ArgumentException>().WithMessage("*aliases*");
    }

    [Theory]
    [InlineData("release-control")]
    [InlineData("stage-control")]
    [InlineData("release-boundary-whitespace")]
    [InlineData("stage-over-limit")]
    public void SealExecutionBinding_ShouldRejectNonCanonicalAdmissionIdentifiers(string invalidIdentifier)
    {
        var binding = BuildExecutionBinding();
        switch (invalidIdentifier)
        {
            case "release-control":
                binding.Admission.RolloutRelease = "release-alpha\nhostile-instruction";
                break;
            case "stage-control":
                binding.Admission.RolloutStage = "canary\rhostile-instruction";
                break;
            case "release-boundary-whitespace":
                binding.Admission.RolloutRelease = " release-alpha";
                break;
            case "stage-over-limit":
                binding.Admission.RolloutStage = new string('s', 129);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidIdentifier), invalidIdentifier, null);
        }

        var act = () => AgentProfileExecutionBindingCodec.Seal(binding);

        act.Should().Throw<ArgumentException>().WithMessage("*admission provenance*");
    }

    [Fact]
    public void SealExecutionBinding_ShouldAcceptEmptyProfileInstructions()
    {
        var binding = BuildExecutionBinding();
        binding.ProfileInstructions = string.Empty;

        var sealedBinding = AgentProfileExecutionBindingCodec.Seal(binding);

        sealedBinding.ProfileInstructions.Should().BeEmpty();
        AgentProfileExecutionBindingCodec.Verify(sealedBinding).Should().BeTrue();
    }

    [Theory]
    [InlineData("ascii-boundary", true)]
    [InlineData("ascii-over-limit", false)]
    [InlineData("multibyte-boundary", true)]
    [InlineData("multibyte-over-limit", false)]
    public void SealExecutionBinding_ShouldEnforceAuthoritativeProfileInstructionUtf8Limit(
        string contentKind,
        bool expectedValid)
    {
        var limit = ProfileValidationLimits.ProfileInstructionsMaxUtf8Bytes;
        var instructions = contentKind switch
        {
            "ascii-boundary" => new string('a', limit),
            "ascii-over-limit" => new string('a', limit + 1),
            "multibyte-boundary" => new string('\u00E9', limit / 2),
            "multibyte-over-limit" => new string('\u00E9', limit / 2) + "a",
            _ => throw new ArgumentOutOfRangeException(nameof(contentKind), contentKind, null),
        };
        Encoding.UTF8.GetByteCount(instructions).Should().Be(
            expectedValid ? limit : limit + 1);
        var binding = BuildExecutionBinding();
        binding.ProfileInstructions = instructions;

        var act = () => AgentProfileExecutionBindingCodec.Seal(binding);

        if (expectedValid)
        {
            var sealedBinding = act();
            AgentProfileExecutionBindingCodec.Verify(sealedBinding).Should().BeTrue();
        }
        else
        {
            act.Should().Throw<ArgumentException>().WithMessage("*profile instructions*");
            var validBinding = BuildExecutionBinding();
            validBinding.ProfileInstructions = new string('a', limit);
            var tamperedBinding = AgentProfileExecutionBindingCodec.Seal(validBinding);
            tamperedBinding.ProfileInstructions = instructions;
            AgentProfileExecutionBindingCodec.Verify(tamperedBinding).Should().BeFalse();
        }
    }

    [Fact]
    public void ExactReference_ShouldKeepFrozenWireShape()
    {
        ExactRemoteSkillRef.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => (field.Name, field.FieldNumber))
            .Should()
            .Equal(("guid", 1), ("literal_version", 2));
        ExactRemoteSkillRef.Descriptor.Fields.InFieldNumberOrder()
            .Should().NotContain(static field => field.Name == "name");
    }

    [Fact]
    public void AdditiveFields_ShouldRemainAbsentForOldBytes()
    {
        RoleGAgentState.Parser.ParseFrom(Array.Empty<byte>()).AgentProfileBinding.Should().BeNull();
        NyxIdChatConversationCreateCommand.Parser.ParseFrom(Array.Empty<byte>()).AgentProfileBinding.Should().BeNull();
    }

    internal static AgentProfileExecutionBinding BuildExecutionBinding(
        AgentProfileActivationMode activationMode = AgentProfileActivationMode.Enforced,
        long sourceStateVersion = 17,
        long publishedRevision = 5,
        string rolloutStage = "canary",
        string instructionBody = "Use the exact sealed procedure.")
    {
        var member = new AgentProfileExecutionMember
        {
            IntentId = "intent-alpha",
            RoutingDescription = "Handle alpha requests.",
            ActivationMode = AgentProfileExecutionMemberActivationMode.Routed,
            TaskToolPolicy = new AgentProfileToolPolicy { ToolNames = { "task-tool" } },
            SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
            SkillProvenance = new AgentProfileExecutionSkillProvenance
            {
                ExactSkillRef = new ExactRemoteSkillRef
                {
                    Guid = "11111111-1111-1111-1111-111111111111",
                    LiteralVersion = "1.2",
                },
                ExpectedSkillName = "skill-alpha",
                ExpectedPublisherId = "publisher-alpha",
                CanonicalSkillName = "skill-alpha",
                PublisherId = "publisher-alpha",
                UpstreamSkillHash = "upstream-hash-alpha",
                SourceSealedSkillSha256 = Digest(0x41),
            },
            InstructionBody = instructionBody,
            InstructionBodySha256 = ByteString.CopyFrom(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instructionBody))),
        };
        member.ExplicitTriggerAliases.Add("alpha");

        var binding = new AgentProfileExecutionBinding
        {
            Source = new AgentProfileExecutionSourceProvenance
            {
                ProfileId = "profile-alpha",
                StateVersion = sourceStateVersion,
                PublishedRevision = publishedRevision,
                PublishedSnapshotSha256 = Digest(0x21),
            },
            Admission = new AgentProfileExecutionAdmissionProvenance
            {
                RolloutRelease = "nyxid-chat-r7",
                RolloutStage = rolloutStage,
                ActivationMode = activationMode,
                RouteToolSetRef = "nyxid.profile.route",
                AdmissionSha256 = Digest(0x31),
            },
            EffectiveMaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "recovery-tool", "task-tool" },
            },
            EffectiveRecoveryToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { "recovery-tool" },
            },
            ProfileInstructions = "Follow the published profile instructions.",
            RuntimeBounds = new AgentProfileExecutionRuntimeBounds
            {
                MaxPlanSteps = 4,
                HandoffTtlSeconds = 900,
                ClassifierTimeoutMs = 600,
                MaxSelectedSkillBytes = 24_576,
            },
        };
        binding.Members.Add(member);
        return binding;
    }

    internal static void ConfigureAlwaysMember(
        AgentProfileExecutionMember member,
        string instructionBody)
    {
        member.ActivationMode = AgentProfileExecutionMemberActivationMode.Always;
        member.IntentId = string.Empty;
        member.RoutingDescription = string.Empty;
        member.ExplicitTriggerAliases.Clear();
        member.TaskToolPolicy = null;
        member.SideEffectClass = AgentProfileSideEffectClass.Unspecified;
        member.InstructionBody = instructionBody;
        member.InstructionBodySha256 = ByteString.CopyFrom(
            SHA256.HashData(Encoding.UTF8.GetBytes(instructionBody)));
    }

    private static AgentProfileExecutionBinding BuildRawAggregateBinding(int extraBodyBytes)
    {
        const int memberBodyBytes = 16_384;
        var binding = BuildExecutionBinding();
        binding.ProfileInstructions = new string(
            'p',
            AgentProfileExecutionBindingLimits.ProfileInstructionsMaxUtf8Bytes);
        binding.Members[0].InstructionBody = new string('a', memberBodyBytes);
        binding.Members[0].InstructionBodySha256 = ByteString.CopyFrom(
            SHA256.HashData(Encoding.UTF8.GetBytes(binding.Members[0].InstructionBody)));
        var second = binding.Members[0].Clone();
        second.IntentId = "intent-beta";
        second.RoutingDescription = "Handle beta requests.";
        second.ExplicitTriggerAliases.Clear();
        second.ExplicitTriggerAliases.Add("beta");
        second.SkillProvenance.ExactSkillRef.Guid = "22222222-2222-2222-2222-222222222222";
        second.InstructionBody = new string('b', memberBodyBytes + extraBodyBytes);
        second.InstructionBodySha256 = ByteString.CopyFrom(
            SHA256.HashData(Encoding.UTF8.GetBytes(second.InstructionBody)));
        binding.Members.Add(second);
        return binding;
    }

    internal static RenderedProfileLayerBoundary BuildRenderedProfileLayerBoundary(int extraRenderedByte)
    {
        const string profileInstructions = "profile";
        const string firstProcedure = "first";
        const string opening = "<always-skill-procedure>\n";
        const string closing = "\n</always-skill-procedure>";
        const string separator = "\n\n";
        var fixedRendered = string.Concat(
            profileInstructions,
            separator,
            opening,
            firstProcedure,
            closing,
            separator,
            opening,
            closing);
        var secondProcedureBytes =
            AgentProfileExecutionBindingLimits.MaterializedProfileLayerMaxUtf8Bytes -
            Encoding.UTF8.GetByteCount(fixedRendered) +
            extraRenderedByte;
        var secondProcedure = new string('b', secondProcedureBytes);
        var rendered = string.Concat(
            profileInstructions,
            separator,
            opening,
            firstProcedure,
            closing,
            separator,
            opening,
            secondProcedure,
            closing);
        return new RenderedProfileLayerBoundary(
            profileInstructions,
            [firstProcedure, secondProcedure],
            rendered);
    }

    internal sealed record RenderedProfileLayerBoundary(
        string ProfileInstructions,
        IReadOnlyList<string> AlwaysProcedures,
        string Rendered);

    private static ByteString Digest(byte value) => ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());
}
