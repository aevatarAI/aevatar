using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Mainnet.Host.Api.Profiles;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetAgentProfileRolloutSelectorTests
{
    [Fact]
    public void Disabled_gate_should_select_no_profile_without_reading_an_artifact()
    {
        var selector = CreateSelector(new Dictionary<string, string?>());

        selector.GetSnapshotForNewConversation("conversation-a").Should().BeNull();
        selector.Should().BeAssignableTo<INyxIdChatAgentProfileSnapshotSource>();
    }

    [Fact]
    public void Disabled_gate_should_reject_nonzero_cohort()
    {
        var act = () => CreateSelector(new Dictionary<string, string?>
        {
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:CohortBasisPoints"] = "500",
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*CohortBasisPoints=0*");
    }

    [Fact]
    public void Stable_bucket_should_depend_only_on_profile_version_and_actor_id()
    {
        var first = MainnetAgentProfileRolloutSelector.ComputeBucket("profile-v1", "conversation-a");
        var second = MainnetAgentProfileRolloutSelector.ComputeBucket("profile-v1", "conversation-a");

        first.Should().Be(5512);
        second.Should().Be(5512);
        MainnetAgentProfileRolloutSelector.ComputeBucket("profile-v2", "conversation-a")
            .Should().Be(9098);
    }

    [Fact]
    public void Full_cohort_should_return_a_defensive_copy_of_the_reviewed_profile()
    {
        using var artifact = ProfileArtifactScope.Create(BuildValidProfileJson());
        var selector = CreateSelector(new Dictionary<string, string?>
        {
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:NewBindingsEnabled"] = "true",
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:CohortBasisPoints"] = "10000",
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:ReviewedProfilePath"] = artifact.Path,
        });

        var first = selector.GetSnapshotForNewConversation("conversation-a");
        var second = selector.GetSnapshotForNewConversation("conversation-a");

        first.Should().NotBeNull();
        first!.ActivationMode.Should().Be(AgentProfileActivationMode.Shadow);
        first.ProfileVersion.Should().Be("nyxid-chat-shadow-v1");
        AgentProfileSnapshotCodec.ByteEquivalent(first, second!).Should().BeTrue();
        first.Should().NotBeSameAs(second);
        AgentProfileSnapshotCodec.Verify(first).Should().BeTrue();
    }

    [Fact]
    public void Full_cohort_should_keep_the_startup_snapshot_immutable_across_callers()
    {
        using var artifact = ProfileArtifactScope.Create(BuildValidProfileJson());
        var selector = CreateSelector(new Dictionary<string, string?>
        {
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:NewBindingsEnabled"] = "true",
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:CohortBasisPoints"] = "10000",
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:ReviewedProfilePath"] = artifact.Path,
        });
        var first = selector.GetSnapshotForNewConversation("conversation-a")!;
        first.ProfileVersion = "caller-mutated-version";

        var second = selector.GetSnapshotForNewConversation("conversation-a");

        second.Should().NotBeNull();
        second!.ProfileVersion.Should().Be("nyxid-chat-shadow-v1");
        AgentProfileSnapshotCodec.Verify(second).Should().BeTrue();
    }

    [Fact]
    public void Enabled_gate_should_fail_closed_on_mutable_or_unresolved_profile_values()
    {
        using var artifact = ProfileArtifactScope.Create(
            BuildValidProfileJson().Replace("1.2", "latest", StringComparison.Ordinal));
        var act = () => CreateSelector(new Dictionary<string, string?>
        {
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:NewBindingsEnabled"] = "true",
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:CohortBasisPoints"] = "500",
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:ReviewedProfilePath"] = artifact.Path,
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*unresolved or mutable*");
    }

    [Fact]
    public void Enabled_gate_should_fail_closed_on_zero_policy_hash()
    {
        using var artifact = ProfileArtifactScope.Create(BuildValidProfileJson(policyHashByte: 0));
        var act = () => CreateSelector(new Dictionary<string, string?>
        {
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:NewBindingsEnabled"] = "true",
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:CohortBasisPoints"] = "500",
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:ReviewedProfilePath"] = artifact.Path,
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*policy hash*");
    }

    [Theory]
    [InlineData(
        "AGENT_PROFILE_SIDE_EFFECT_CLASS_READ_ONLY",
        "AGENT_PROFILE_SIDE_EFFECT_CLASS_UNSPECIFIED",
        "sideEffectClass")]
    [InlineData(
        "00000000-0000-0000-0000-000000000004",
        "00000000-0000-0000-0000-000000000001",
        "exact skill references")]
    [InlineData(
        "5d0d7b72-acff-49af-bb1b-9f30bbb7c102",
        "not-a-guid",
        "reviewedPublisherId")]
    [InlineData(
        "\"request\"",
        "\"api_key\"",
        "forbidden")]
    public void Enabled_gate_should_reject_invalid_reviewed_profile_contract(
        string oldValue,
        string newValue,
        string expectedError)
    {
        using var artifact = ProfileArtifactScope.Create(
            BuildValidProfileJson().Replace(oldValue, newValue, StringComparison.Ordinal));
        var act = () => CreateSelector(new Dictionary<string, string?>
        {
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:NewBindingsEnabled"] = "true",
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:CohortBasisPoints"] = "500",
            [$"{MainnetAgentProfileRolloutOptions.SectionName}:ReviewedProfilePath"] = artifact.Path,
        });

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expectedError}*");
    }

    private static MainnetAgentProfileRolloutSelector CreateSelector(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return MainnetAgentProfileRolloutSelector.Create(configuration, Directory.GetCurrentDirectory());
    }

    private static string BuildValidProfileJson(byte policyHashByte = 1)
    {
        var maximumTools = new[]
        {
            "ornn_search_skills", "use_skill", "inventory", "catalog", "status",
            "handoff", "update", "route", "delete", "request",
        };
        var profile = new AgentProfileSnapshot
        {
            ProfileId = "nyxid-chat",
            ProfileVersion = "nyxid-chat-shadow-v1",
            AgentKind = "nyxid.chat",
            PolicyRevision = "policy-v1",
            SkillsetProvenance = new ExactRemoteSkillsetRef
            {
                Guid = "10000000-0000-0000-0000-000000000000",
                LiteralVersion = "1.0",
            },
            RouteToolSetRef = "profile.route.v1",
            MaximumToolPolicy = new AgentProfileToolPolicy(),
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            MaxPlanSteps = 4,
            HandoffTtlSeconds = 900,
            ClassifierTimeoutMs = 600,
            ExactSkillFetchTimeoutMs = 1500,
            MaxSelectedSkillBytes = 24576,
            ActivationMode = AgentProfileActivationMode.Shadow,
        };
        profile.MaximumToolPolicy.ToolNames.AddRange(maximumTools);
        profile.MaximumToolPolicy.ToolSetRefs.Add("connected.services");
        profile.RecoveryToolPolicy.ToolNames.AddRange(
            ["ornn_search_skills", "use_skill", "inventory", "catalog", "status"]);
        foreach (var index in Enumerable.Range(1, 4))
        {
            var member = new AgentProfileSkillMember
            {
                IntentId = $"intent_{index}",
                RoutingDescription = $"route {index}",
                SkillRef = new ExactRemoteSkillRef
                {
                    Guid = $"00000000-0000-0000-0000-00000000000{index}",
                    LiteralVersion = "1.2",
                },
                TaskToolPolicy = new AgentProfileToolPolicy(),
                SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
                ExpectedSkillName = $"fixture-skill-{index}",
                ReviewedPublisherId = "5d0d7b72-acff-49af-bb1b-9f30bbb7c102",
            };
            member.ExplicitTriggerAliases.Add($"alias-{index}");
            member.TaskToolPolicy.ToolNames.Add("inventory");
            profile.Members.Add(member);
        }

        var sealedProfile = AgentProfileSnapshotCodec.Seal(profile);
        if (policyHashByte == 0)
            sealedProfile.DeterministicPolicySha256 = ByteString.CopyFrom(new byte[32]);
        return JsonFormatter.Default.Format(sealedProfile);
    }

    private sealed class ProfileArtifactScope : IDisposable
    {
        private ProfileArtifactScope(string path) => Path = path;
        public string Path { get; }

        public static ProfileArtifactScope Create(string json)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aevatar-profile-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return new ProfileArtifactScope(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
