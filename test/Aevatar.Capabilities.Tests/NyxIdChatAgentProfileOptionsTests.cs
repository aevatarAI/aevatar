using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Options;

namespace Aevatar.Capabilities.Tests;

public sealed class NyxIdChatAgentProfileOptionsTests
{
    private const string CanonicalGuid = "550e8400-e29b-41d4-a716-446655440000";
    private const string NonzeroVersionZeroGuid = "00000000-0000-0000-0000-000000000001";
    private const string NilGuid = "00000000-0000-0000-0000-000000000000";

    public static TheoryData<bool, string> ValidExactGuids => new()
    {
        { false, CanonicalGuid },
        { true, CanonicalGuid },
        { false, NonzeroVersionZeroGuid },
        { true, NonzeroVersionZeroGuid },
    };

    public static TheoryData<bool, string> InvalidExactGuids => new()
    {
        { false, "" },
        { true, "" },
        { false, " " },
        { true, " " },
        { false, $" {CanonicalGuid}" },
        { true, $"{CanonicalGuid} " },
        { false, CanonicalGuid.ToUpperInvariant() },
        { true, CanonicalGuid.ToUpperInvariant() },
        { false, "550e8400e29b41d4a716446655440000" },
        { true, "{550e8400-e29b-41d4-a716-446655440000}" },
        { false, "(550e8400-e29b-41d4-a716-446655440000)" },
        { true, "550e8400-e29b-41d4-a716-44665544000" },
        { false, "550e8400-e29b-41d4-a716-44665544000z" },
    };

    [Fact]
    public void Validate_ShouldAcceptDefaultDisabledSlotWithEmptyBaseline()
    {
        var result = CreateValidator(new NyxIdChatAgentProfileValidationBaseline([], []))
            .Validate(null, new NyxIdChatAgentProfileOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldRejectDisabledProfilePayloadWithEmptyBaseline()
    {
        var result = CreateValidator(new NyxIdChatAgentProfileValidationBaseline([], []))
            .Validate(null, new NyxIdChatAgentProfileOptions
            {
                Enabled = false,
                Profile = BuildValidProfile(),
            });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message => message.Contains("RequiredRecoveryToolNames", StringComparison.Ordinal));
        result.Failures.Should().Contain(message => message.Contains("DeniedLegacyToolNames", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Validate_ShouldRequireBothBaselineSetsForEnabledProfile(
        bool emptyRequired,
        bool emptyDenied)
    {
        var baseline = new NyxIdChatAgentProfileValidationBaseline(
            emptyRequired ? [] : ["recover_tool"],
            emptyDenied ? [] : ["legacy_tool"]);

        var result = CreateValidator(baseline).Validate(null, EnabledOptions());

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldAcceptReviewedBaselineAndCompleteProfile()
    {
        var result = CreateValidator(ReviewedBaseline()).Validate(null, EnabledOptions());

        result.Succeeded.Should().BeTrue(string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Fact]
    public void Validate_ShouldRejectExternalReferenceDrift()
    {
        var options = new NyxIdChatAgentProfileOptions
        {
            ExternalReference = "drifted-reference",
        };

        var result = CreateValidator(new NyxIdChatAgentProfileValidationBaseline([], []))
            .Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains(nameof(options.ExternalReference), StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectEnabledSlotWithoutProfilePayload()
    {
        var result = CreateValidator(ReviewedBaseline()).Validate(
            null,
            new NyxIdChatAgentProfileOptions { Enabled = true });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains("requires a complete Profile payload", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("agent-kind", "AgentKind")]
    [InlineData("preset-digest", "DeterministicPolicySha256")]
    public void Validate_ShouldRejectInvalidConfigurationOwnedProfileFields(
        string mutation,
        string expectedFailure)
    {
        var options = EnabledOptions();
        if (mutation == "agent-kind")
            options.Profile!.AgentKind = "other.agent";
        else
            options.Profile!.DeterministicPolicySha256 = ByteString.CopyFrom(new byte[32]);

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "SkillsetProvenance")]
    [InlineData(false, "SkillRef")]
    public void Validate_ShouldRejectMissingExactReference(bool skillsetRef, string expectedFailure)
    {
        var options = EnabledOptions();
        if (skillsetRef)
            options.Profile!.SkillsetProvenance = null;
        else
            options.Profile!.Members[0].SkillRef = null;

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains(expectedFailure, StringComparison.Ordinal) &&
            message.Contains("is required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "ProfileId")]
    [InlineData(false, "IntentId")]
    public void Validate_ShouldRejectNoncanonicalIdentifier(bool profileIdentifier, string expectedFailure)
    {
        var options = EnabledOptions();
        if (profileIdentifier)
            options.Profile!.ProfileId = "not canonical";
        else
            options.Profile!.Members[0].IntentId = "not canonical";

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains(expectedFailure, StringComparison.Ordinal) &&
            message.Contains("invalid canonical form", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "RouteToolSetRef")]
    [InlineData(false, "MaximumToolPolicy.ToolNames")]
    public void Validate_ShouldRejectInvalidToolOrToolSetName(bool routeToolSet, string expectedFailure)
    {
        var options = EnabledOptions();
        if (routeToolSet)
            options.Profile!.RouteToolSetRef = "not a tool set";
        else
            options.Profile!.MaximumToolPolicy.ToolNames.Add("not a tool");

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains(expectedFailure, StringComparison.Ordinal) &&
            message.Contains("invalid tool or tool-set name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "is required")]
    [InlineData(" revision-1", "leading or trailing whitespace")]
    [InlineData("revision-1 ", "leading or trailing whitespace")]
    public void Validate_ShouldRejectMissingOrPaddedRequiredString(string policyRevision, string expectedFailure)
    {
        var options = EnabledOptions();
        options.Profile!.PolicyRevision = policyRevision;

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains(nameof(AgentProfileSnapshot.PolicyRevision), StringComparison.Ordinal) &&
            message.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Validate_ShouldEnforceRequiredStringUtf8ByteBoundary(
        bool appendAsciiByte,
        bool expectedValid)
    {
        var options = EnabledOptions();
        options.Profile!.PolicyRevision = new string('\u00e9', 64) + (appendAsciiByte ? "a" : string.Empty);

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        if (expectedValid)
            result.Succeeded.Should().BeTrue(string.Join(Environment.NewLine, result.Failures ?? []));
        else
            result.Failures.Should().Contain(message =>
                message.Contains(nameof(AgentProfileSnapshot.PolicyRevision), StringComparison.Ordinal) &&
                message.Contains("cannot exceed 128 UTF-8 bytes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void Validate_ShouldEnforceMemberCountBoundaries(int memberCount, bool expectedValid)
    {
        var options = EnabledOptions();
        SetMemberCount(options.Profile!, memberCount);

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        if (expectedValid)
            result.Succeeded.Should().BeTrue(string.Join(Environment.NewLine, result.Failures ?? []));
        else
            result.Failures.Should().Contain(message =>
                message.Contains("Members must contain between 1 and 32 entries", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(65_535, true)]
    [InlineData(65_536, true)]
    [InlineData(65_537, false)]
    public void Validate_ShouldEnforceSealedSnapshotSizeBoundary(int sealedSize, bool expectedValid)
    {
        var options = EnabledOptions();
        options.Profile = BuildProfileWithSealedSize(sealedSize);

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        if (expectedValid)
            result.Succeeded.Should().BeTrue(string.Join(Environment.NewLine, result.Failures ?? []));
        else
            result.Failures.Should().Contain(message =>
                message.Contains("sealed profile snapshot cannot exceed 65536 bytes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("duplicate-intent", "IntentId", "unique within the profile")]
    [InlineData("duplicate-ref", "SkillRef", "unique within the profile")]
    [InlineData("duplicate-alias", "ExplicitTriggerAliases", "globally unique ignoring case")]
    [InlineData("alias-limit", "ExplicitTriggerAliases", "cannot exceed 16 entries")]
    [InlineData("side-effect", "SideEffectClass", "must be explicit")]
    public void Validate_ShouldRejectInvalidMemberIdentityAndBounds(
        string mutation,
        string fieldName,
        string reason)
    {
        var options = EnabledOptions();
        var profile = options.Profile!;
        switch (mutation)
        {
            case "duplicate-intent":
            {
                var duplicate = BuildMember(1);
                duplicate.IntentId = profile.Members[0].IntentId;
                profile.Members.Add(duplicate);
                break;
            }
            case "duplicate-ref":
            {
                var duplicate = BuildMember(1);
                duplicate.SkillRef = profile.Members[0].SkillRef.Clone();
                profile.Members.Add(duplicate);
                break;
            }
            case "duplicate-alias":
            {
                var duplicate = BuildMember(1);
                duplicate.ExplicitTriggerAliases.Clear();
                duplicate.ExplicitTriggerAliases.Add(
                    profile.Members[0].ExplicitTriggerAliases[0].ToUpperInvariant());
                profile.Members.Add(duplicate);
                break;
            }
            case "alias-limit":
                for (var index = 1; index <= 16; index++)
                    profile.Members[0].ExplicitTriggerAliases.Add($"alias-{index}");
                break;
            default:
                profile.Members[0].SideEffectClass = AgentProfileSideEffectClass.Unspecified;
                break;
        }

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains(fieldName, StringComparison.Ordinal) &&
            message.Contains(reason, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MaxPlanSteps", 3, false)]
    [InlineData("MaxPlanSteps", 4, true)]
    [InlineData("MaxPlanSteps", 5, false)]
    [InlineData("HandoffTtlSeconds", 899, false)]
    [InlineData("HandoffTtlSeconds", 900, true)]
    [InlineData("HandoffTtlSeconds", 901, false)]
    [InlineData("ClassifierTimeoutMs", 599, false)]
    [InlineData("ClassifierTimeoutMs", 600, true)]
    [InlineData("ClassifierTimeoutMs", 601, false)]
    [InlineData("ExactSkillFetchTimeoutMs", 1_499, false)]
    [InlineData("ExactSkillFetchTimeoutMs", 1_500, true)]
    [InlineData("ExactSkillFetchTimeoutMs", 1_501, false)]
    [InlineData("MaxSelectedSkillBytes", 24_575, false)]
    [InlineData("MaxSelectedSkillBytes", 24_576, true)]
    [InlineData("MaxSelectedSkillBytes", 24_577, false)]
    public void Validate_ShouldEnforceRuntimeParameterBoundaries(
        string parameter,
        int value,
        bool expectedValid)
    {
        var options = EnabledOptions();
        switch (parameter)
        {
            case "MaxPlanSteps":
                options.Profile!.MaxPlanSteps = value;
                break;
            case "HandoffTtlSeconds":
                options.Profile!.HandoffTtlSeconds = value;
                break;
            case "ClassifierTimeoutMs":
                options.Profile!.ClassifierTimeoutMs = value;
                break;
            case "ExactSkillFetchTimeoutMs":
                options.Profile!.ExactSkillFetchTimeoutMs = value;
                break;
            default:
                options.Profile!.MaxSelectedSkillBytes = value;
                break;
        }

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        if (expectedValid)
            result.Succeeded.Should().BeTrue(string.Join(Environment.NewLine, result.Failures ?? []));
        else
            result.Failures.Should().Contain(message =>
                message.Contains(parameter, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(599, false)]
    [InlineData(600, false)]
    [InlineData(601, true)]
    public void Validate_ShouldRejectOnlyColdPreTurnBudgetsAboveMaximum(
        int classifierTimeoutMs,
        bool expectedBudgetFailure)
    {
        var options = EnabledOptions();
        options.Profile!.ClassifierTimeoutMs = classifierTimeoutMs;

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);
        var hasBudgetFailure = result.Failures?.Any(message =>
            message.Contains("cold pre-turn budget", StringComparison.Ordinal)) == true;

        hasBudgetFailure.Should().Be(expectedBudgetFailure);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Validate_ShouldAcceptOnlyExplicitActivationModes(int activationMode, bool expectedValid)
    {
        var options = EnabledOptions();
        options.Profile!.ActivationMode = (AgentProfileActivationMode)activationMode;

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        if (expectedValid)
            result.Succeeded.Should().BeTrue(string.Join(Environment.NewLine, result.Failures ?? []));
        else
            result.Failures.Should().Contain(message =>
                message.Contains("ActivationMode must be Shadow or Enforced", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("maximum", "MaximumToolPolicy")]
    [InlineData("recovery", "RecoveryToolPolicy")]
    [InlineData("member", "TaskToolPolicy")]
    public void Validate_ShouldRejectMissingPolicy(string policy, string expectedFailure)
    {
        var options = EnabledOptions();
        switch (policy)
        {
            case "maximum":
                options.Profile!.MaximumToolPolicy = null;
                break;
            case "recovery":
                options.Profile!.RecoveryToolPolicy = null;
                break;
            default:
                options.Profile!.Members[0].TaskToolPolicy = null;
                break;
        }

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains(expectedFailure, StringComparison.Ordinal) &&
            message.Contains("is required", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, 63, true)]
    [InlineData(false, 64, true)]
    [InlineData(false, 65, false)]
    [InlineData(true, 63, true)]
    [InlineData(true, 64, true)]
    [InlineData(true, 65, false)]
    public void Validate_ShouldEnforcePolicyEntryBoundaries(
        bool toolSetRefs,
        int entryCount,
        bool expectedValid)
    {
        var options = EnabledOptions();
        var registeredToolSets = new List<string> { "profile.route" };
        if (toolSetRefs)
        {
            for (var index = 0; index < entryCount; index++)
            {
                var name = $"set.{index}";
                options.Profile!.MaximumToolPolicy.ToolSetRefs.Add(name);
                registeredToolSets.Add(name);
            }
        }
        else
        {
            options.Profile!.MaximumToolPolicy.ToolNames.Clear();
            options.Profile.MaximumToolPolicy.ToolNames.Add(["recover_tool", "task_tool"]);
            for (var index = 2; index < entryCount; index++)
                options.Profile.MaximumToolPolicy.ToolNames.Add($"tool_{index}");
        }

        var result = CreateValidator(ReviewedBaseline(), registeredToolSets).Validate(null, options);

        if (expectedValid)
            result.Succeeded.Should().BeTrue(string.Join(Environment.NewLine, result.Failures ?? []));
        else
            result.Failures.Should().Contain(message =>
                message.Contains(toolSetRefs ? "ToolSetRefs" : "ToolNames", StringComparison.Ordinal) &&
                message.Contains("cannot exceed 64 entries", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("duplicate", "duplicate values")]
    [InlineData("unknown", "unknown tool set")]
    public void Validate_ShouldRejectDuplicateOrUnknownPolicyToolSetRef(
        string mutation,
        string expectedFailure)
    {
        var options = EnabledOptions();
        options.Profile!.MaximumToolPolicy.ToolSetRefs.Add("profile.route");
        options.Profile.MaximumToolPolicy.ToolSetRefs.Add(
            mutation == "duplicate" ? "profile.route" : "missing.route");

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [MemberData(nameof(ValidExactGuids))]
    [Theory]
    public void Validate_ShouldAcceptCanonicalNonzeroGuidForBothExactRefKinds(
        bool skillsetRef,
        string guid)
    {
        var options = EnabledOptions();
        SetExactGuid(options.Profile!, skillsetRef, guid);

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Succeeded.Should().BeTrue(string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [MemberData(nameof(InvalidExactGuids))]
    [Theory]
    public void Validate_ShouldRejectNoncanonicalGuidForBothExactRefKinds(
        bool skillsetRef,
        string guid)
    {
        var options = EnabledOptions();
        SetExactGuid(options.Profile!, skillsetRef, guid);

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message => message.Contains("nonzero canonical lowercase D GUID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_ShouldRejectNilGuidForBothExactRefKinds(bool skillsetRef)
    {
        var options = EnabledOptions();
        SetExactGuid(options.Profile!, skillsetRef, NilGuid);

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message => message.Contains("nonzero canonical lowercase D GUID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0.0")]
    [InlineData("latest")]
    [InlineData("^1.0")]
    [InlineData("01.0")]
    public void Validate_ShouldRejectNonliteralOrnnVersion(string literalVersion)
    {
        var options = EnabledOptions();
        options.Profile!.Members[0].SkillRef.LiteralVersion = literalVersion;

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message => message.Contains("<major>.<minor>", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectMissingRequiredRecoveryTool()
    {
        var baseline = new NyxIdChatAgentProfileValidationBaseline(["missing_tool"], ["legacy_tool"]);

        var result = CreateValidator(baseline).Validate(null, EnabledOptions());

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message => message.Contains("missing from the maximum policy", StringComparison.Ordinal));
        result.Failures.Should().Contain(message => message.Contains("missing from the recovery policy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("maximum")]
    [InlineData("recovery")]
    [InlineData("member")]
    public void Validate_ShouldRejectDeniedLegacyToolInEveryPolicy(string policy)
    {
        var options = EnabledOptions();
        var target = policy switch
        {
            "maximum" => options.Profile!.MaximumToolPolicy,
            "recovery" => options.Profile!.RecoveryToolPolicy,
            _ => options.Profile!.Members[0].TaskToolPolicy,
        };
        target.ToolNames.Add("legacy_tool");

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message => message.Contains("Denied legacy tool", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectNonNfcAliasAndDuplicateToolNamesIgnoringCase()
    {
        var options = EnabledOptions();
        options.Profile!.Members[0].ExplicitTriggerAliases.Add("e\u0301");
        options.Profile.MaximumToolPolicy.ToolNames.Add("RECOVER_TOOL");

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message => message.Contains("Unicode NFC", StringComparison.Ordinal));
        result.Failures.Should().Contain(message => message.Contains("duplicates ignoring case", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectUnknownRouteToolSetAndNonProperMemberPolicy()
    {
        var options = EnabledOptions();
        options.Profile!.RouteToolSetRef = "missing.route";
        options.Profile.Members[0].TaskToolPolicy = options.Profile.MaximumToolPolicy.Clone();

        var result = CreateValidator(ReviewedBaseline()).Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message => message.Contains("unknown tool set", StringComparison.Ordinal));
        result.Failures.Should().Contain(message => message.Contains("proper subset", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidationBaseline_ShouldDefensivelyCopyInputWithoutChangingComparerSemantics()
    {
        var required = new[] { "recover_tool" };
        var denied = new[] { "legacy_tool" };
        var baseline = new NyxIdChatAgentProfileValidationBaseline(required, denied);
        required[0] = "mutated";
        denied[0] = "mutated";

        baseline.RequiredRecoveryToolNames.Should().Equal("recover_tool");
        baseline.DeniedLegacyToolNames.Should().Equal("legacy_tool");
    }

    [Fact]
    public void ValidationBaseline_ShouldRejectDuplicateNamesIgnoringCase()
    {
        var act = () => new NyxIdChatAgentProfileValidationBaseline(
            ["recover_tool", "RECOVER_TOOL"],
            ["legacy_tool"]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ProductionSchemaScanner_ShouldAcceptAllThreeBoundedRoots()
    {
        AgentProfileProductionSchemaScanner.FindForbiddenNames().Should().BeEmpty();
        typeof(NyxIdChatAgentProfileOptions).GetProperty(nameof(NyxIdChatAgentProfileOptions.Profile)).Should().NotBeNull();
        typeof(NyxIdChatAgentProfileValidationBaseline)
            .GetProperty(nameof(NyxIdChatAgentProfileValidationBaseline.RequiredRecoveryToolNames))
            .Should()
            .NotBeNull();
    }

    [Theory]
    [InlineData("api_token", true)]
    [InlineData("SkillPayload", true)]
    [InlineData("ProfileVersion", false)]
    public void ProductionSchemaScanner_ShouldDetectForbiddenIdentifierTokens(
        string identifier,
        bool expectedForbidden)
    {
        AgentProfileProductionSchemaScanner.IsForbiddenIdentifier(identifier)
            .Should()
            .Be(expectedForbidden);
    }

    [Fact]
    public void MainnetSnapshotSource_ShouldFreezeAndCloneEnabledProfile()
    {
        var configuredProfile = BuildValidProfile();
        var source = new MainnetNyxIdChatAgentProfileSnapshotSource(
            Options.Create(new NyxIdChatAgentProfileOptions
            {
                Enabled = true,
                Profile = configuredProfile,
            }));
        configuredProfile.ProfileVersion = "mutated-after-startup";

        var first = source.GetSnapshotForNewConversation();
        var second = source.GetSnapshotForNewConversation();
        first!.ProfileVersion = "caller-mutation";

        second.Should().NotBeNull();
        second!.ProfileVersion.Should().Be("profile-v1");
        second.Should().NotBeSameAs(first);
        AgentProfileSnapshotCodec.Verify(second).Should().BeTrue();
    }

    private static NyxIdChatAgentProfileOptionsValidator CreateValidator(
        NyxIdChatAgentProfileValidationBaseline baseline,
        IReadOnlyList<string>? registeredToolSets = null) =>
        new(new FixedToolSetRegistry(registeredToolSets ?? ["profile.route"]), baseline);

    private static NyxIdChatAgentProfileValidationBaseline ReviewedBaseline() =>
        new(["recover_tool"], ["legacy_tool"]);

    private static NyxIdChatAgentProfileOptions EnabledOptions() => new()
    {
        Enabled = true,
        Profile = BuildValidProfile(),
    };

    private static AgentProfileSnapshot BuildValidProfile() => new()
    {
        ProfileId = "profile-alpha",
        ProfileVersion = "profile-v1",
        AgentKind = "nyxid.chat",
        PolicyRevision = "revision-1",
        SkillsetProvenance = new ExactRemoteSkillsetRef
        {
            Guid = CanonicalGuid,
            LiteralVersion = "1.0",
        },
        RouteToolSetRef = "profile.route",
        MaximumToolPolicy = new AgentProfileToolPolicy
        {
            ToolNames = { "recover_tool", "task_tool" },
        },
        RecoveryToolPolicy = new AgentProfileToolPolicy
        {
            ToolNames = { "recover_tool" },
        },
        Members =
        {
            BuildMember(0),
        },
        MaxPlanSteps = 4,
        HandoffTtlSeconds = 900,
        ClassifierTimeoutMs = 600,
        ExactSkillFetchTimeoutMs = 1_500,
        MaxSelectedSkillBytes = 24_576,
        ActivationMode = AgentProfileActivationMode.Enforced,
    };

    private static AgentProfileSkillMember BuildMember(int index) => new()
    {
        IntentId = $"intent-{index}",
        RoutingDescription = $"Route intent {index} requests.",
        SkillRef = new ExactRemoteSkillRef
        {
            Guid = $"00000000-0000-0000-0000-{index + 1:000000000000}",
            LiteralVersion = "1.0",
        },
        ExplicitTriggerAliases = { $"alias-{index}" },
        TaskToolPolicy = new AgentProfileToolPolicy
        {
            ToolNames = { "task_tool" },
        },
        SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
        ExpectedSkillName = $"skill-{index}",
        ReviewedPublisherId = $"publisher-{index}",
    };

    private static void SetMemberCount(AgentProfileSnapshot profile, int memberCount)
    {
        profile.Members.Clear();
        for (var index = 0; index < memberCount; index++)
            profile.Members.Add(BuildMember(index));
    }

    private static AgentProfileSnapshot BuildProfileWithSealedSize(int targetSize)
    {
        var profile = BuildValidProfile();
        var baselineSize = AgentProfileSnapshotCodec.Seal(profile).CalculateSize();
        var firstPayloadLength = Math.Max(0, targetSize - baselineSize - 12);
        var lastPayloadLength = targetSize - baselineSize;
        for (var payloadLength = firstPayloadLength; payloadLength <= lastPayloadLength; payloadLength++)
        {
            using var stream = new MemoryStream();
            stream.Write(profile.ToByteArray());
            using (var output = new CodedOutputStream(stream, leaveOpen: true))
            {
                output.WriteTag(100, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(new byte[payloadLength]));
                output.Flush();
            }

            var candidate = AgentProfileSnapshot.Parser.ParseFrom(stream.ToArray());
            if (AgentProfileSnapshotCodec.Seal(candidate).CalculateSize() == targetSize)
                return candidate;
        }

        throw new InvalidOperationException($"Could not construct a profile with sealed size {targetSize}.");
    }

    private static void SetExactGuid(AgentProfileSnapshot profile, bool skillsetRef, string guid)
    {
        if (skillsetRef)
            profile.SkillsetProvenance.Guid = guid;
        else
            profile.Members[0].SkillRef.Guid = guid;
    }

    private sealed class FixedToolSetRegistry(IReadOnlyList<string> names) : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => names;

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef) =>
            names.Contains(toolSetRef?.Name, StringComparer.Ordinal)
                ? ToolSetResolveResult.Success(toolSetRef!.Name, [])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    toolSetRef?.Name ?? string.Empty,
                    "Unknown test tool set.",
                    names));
    }
}
