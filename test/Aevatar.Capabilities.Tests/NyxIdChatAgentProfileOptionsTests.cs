using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using FluentAssertions;
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
        NyxIdChatAgentProfileValidationBaseline baseline) =>
        new(new FixedToolSetRegistry(["profile.route"]), baseline);

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
            new AgentProfileSkillMember
            {
                IntentId = "intent-alpha",
                RoutingDescription = "Route alpha requests.",
                SkillRef = new ExactRemoteSkillRef
                {
                    Guid = NonzeroVersionZeroGuid,
                    LiteralVersion = "1.0",
                },
                ExplicitTriggerAliases = { "alpha" },
                TaskToolPolicy = new AgentProfileToolPolicy
                {
                    ToolNames = { "task_tool" },
                },
                SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
                ExpectedSkillName = "skill-alpha",
                ReviewedPublisherId = "publisher-alpha",
            },
        },
        MaxPlanSteps = 4,
        HandoffTtlSeconds = 900,
        ClassifierTimeoutMs = 600,
        ExactSkillFetchTimeoutMs = 1_500,
        MaxSelectedSkillBytes = 24_576,
        ActivationMode = AgentProfileActivationMode.Enforced,
    };

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
