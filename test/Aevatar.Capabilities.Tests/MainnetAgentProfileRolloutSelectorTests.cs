using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using Aevatar.Mainnet.Host.Api.Profiles;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetAgentProfileRolloutSelectorTests
{
    [Fact]
    public void Disabled_rollout_should_select_no_admission_without_reading_an_artifact()
    {
        var selector = CreateSelector(new Dictionary<string, string?>());

        selector.SelectForNewConversation("conversation-a").Should().BeNull();
    }

    [Fact]
    public void Disabled_rollout_should_reject_a_dormant_release_spec_path()
    {
        var act = () => CreateSelector(new Dictionary<string, string?>
        {
            [$"{NyxIdChatAgentProfileOptions.SectionName}:ReleaseSpecPath"] = "release.json",
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*cannot configure ReleaseSpecPath*");
    }

    [Fact]
    public void Stable_bucket_should_include_release_stage_cohort_salt_and_actor_id()
    {
        var baseline = MainnetAgentProfileRolloutSelector.ComputeBucket(
            "release-a",
            "canary",
            "salt-a",
            "conversation-a");

        MainnetAgentProfileRolloutSelector.ComputeBucket(
            "release-a",
            "canary",
            "salt-a",
            "conversation-a").Should().Be(baseline);
        MainnetAgentProfileRolloutSelector.ComputeBucket(
            "release-b",
            "canary",
            "salt-a",
            "conversation-a").Should().NotBe(baseline);
        MainnetAgentProfileRolloutSelector.ComputeBucket(
            "release-a",
            "production",
            "salt-a",
            "conversation-a").Should().NotBe(baseline);
        MainnetAgentProfileRolloutSelector.ComputeBucket(
            "release-a",
            "canary",
            "salt-b",
            "conversation-a").Should().NotBe(baseline);
        MainnetAgentProfileRolloutSelector.ComputeBucket(
            "release-a",
            "canary",
            "salt-a",
            "conversation-b").Should().NotBe(baseline);
    }

    [Fact]
    public void Full_cohort_should_return_a_defensive_copy_of_pin_only_release_spec()
    {
        using var artifact = ReleaseSpecArtifactScope.Create(BuildValidReleaseSpecJson());
        var selector = CreateEnabledSelector(artifact.Path);

        var first = selector.SelectForNewConversation("conversation-a");
        var second = selector.SelectForNewConversation("conversation-a");

        first.Should().NotBeNull();
        first!.ReleaseId.Should().Be("nyxid-chat-release-1");
        first.ProfileReference.Should().BeEquivalentTo(new AgentProfileReference
        {
            OwnerHandle = "system",
            ProfileSlug = "nyxid-chat",
        });
        first.Should().NotBeSameAs(second);
        first.ReleaseId = "caller-mutated";
        second!.ReleaseId.Should().Be("nyxid-chat-release-1");
    }

    [Fact]
    public void Release_spec_contract_should_not_be_capable_of_carrying_profile_content()
    {
        AgentProfileRolloutReleaseSpec.Descriptor.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .Should()
            .NotContain(name =>
                name.Contains("instruction", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("routing", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("tool_policy", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("skill_body", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Enabled_rollout_should_reject_unknown_profile_content_fields()
    {
        var validJson = BuildValidReleaseSpecJson();
        using var artifact = ReleaseSpecArtifactScope.Create(
            "{\"instructions\":\"forbidden\"," + validJson[1..]);
        var act = () => CreateEnabledSelector(artifact.Path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*valid ProtoJSON*");
    }

    [Theory]
    [InlineData("system", "other-profile")]
    [InlineData("user-alpha", "nyxid-chat")]
    public void Enabled_rollout_should_require_typed_system_nyxid_chat_reference(
        string ownerHandle,
        string profileSlug)
    {
        var spec = BuildValidReleaseSpec();
        spec.ProfileReference.OwnerHandle = ownerHandle;
        spec.ProfileReference.ProfileSlug = profileSlug;
        using var artifact = ReleaseSpecArtifactScope.Create(Format(spec));
        var act = () => CreateEnabledSelector(artifact.Path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*system/nyxid-chat*");
    }

    [Theory]
    [InlineData(0, 32, 1)]
    [InlineData(7, 31, 1)]
    [InlineData(7, 32, 0)]
    public void Enabled_rollout_should_fail_closed_on_incomplete_admission_pins(
        long publishedRevision,
        int digestLength,
        int closureCount)
    {
        var spec = BuildValidReleaseSpec();
        spec.ExpectedPublishedRevision = publishedRevision;
        spec.ExpectedPublishedSnapshotSha256 = ByteString.CopyFrom(new byte[digestLength]);
        if (closureCount == 0)
            spec.ExpectedExactSkillClosure.Clear();
        using var artifact = ReleaseSpecArtifactScope.Create(Format(spec));
        var act = () => CreateEnabledSelector(artifact.Path);

        act.Should().Throw<InvalidOperationException>();
    }

    private static MainnetAgentProfileRolloutSelector CreateEnabledSelector(string releaseSpecPath) =>
        CreateSelector(new Dictionary<string, string?>
        {
            [$"{NyxIdChatAgentProfileOptions.SectionName}:Enabled"] = "true",
            [$"{NyxIdChatAgentProfileOptions.SectionName}:ReleaseSpecPath"] = releaseSpecPath,
        });

    private static MainnetAgentProfileRolloutSelector CreateSelector(
        IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return MainnetAgentProfileRolloutSelector.Create(
            configuration,
            Directory.GetCurrentDirectory());
    }

    internal static string BuildValidReleaseSpecJson() => Format(BuildValidReleaseSpec());

    internal static AgentProfileRolloutReleaseSpec BuildValidReleaseSpec() => new()
    {
        ReleaseId = "nyxid-chat-release-1",
        Stage = "canary",
        ProfileReference = new AgentProfileReference
        {
            OwnerHandle = "system",
            ProfileSlug = "nyxid-chat",
        },
        ActivationMode = AgentProfileRolloutActivationMode.Enforced,
        CohortSalt = "nyxid-chat-canary-1",
        CohortBasisPoints = 10_000,
        ExpectedPublishedRevision = 7,
        ExpectedPublishedSnapshotSha256 = ByteString.CopyFrom(
            Enumerable.Repeat((byte)0x31, 32).ToArray()),
        ExpectedExactSkillClosure =
        {
            new ExactOrnnSkillReference
            {
                SkillGuid = "11111111-1111-1111-1111-111111111111",
                LiteralVersion = "1.2",
                ExpectedName = "service-call",
                ExpectedPublisherId = "publisher-alpha",
            },
        },
        RuntimeBounds = new AgentProfileRolloutRuntimeBounds
        {
            MaxPlanSteps = 4,
            HandoffTtlSeconds = 900,
            ClassifierTimeoutMs = 600,
            MaxSelectedSkillBytes = 24_576,
        },
    };

    private static string Format(AgentProfileRolloutReleaseSpec spec) =>
        new JsonFormatter(JsonFormatter.Settings.Default.WithIndentation("  ")).Format(spec);

    private sealed class ReleaseSpecArtifactScope : IDisposable
    {
        private ReleaseSpecArtifactScope(string path) => Path = path;

        public string Path { get; }

        public static ReleaseSpecArtifactScope Create(string contents)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aevatar-profile-release-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, contents);
            return new ReleaseSpecArtifactScope(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
