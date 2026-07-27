using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.Tools.AgentProfileRollout;
using Aevatar.Tools.AgentProfileRollout.Contracts;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

[CollectionDefinition("Agent profile rollout environment", DisableParallelization = true)]
public sealed class AgentProfileRolloutEnvironmentCollection;

[Collection("Agent profile rollout environment")]
public sealed class AgentProfileRolloutProvisioningTests
{
    [Fact]
    public void Reviewed_release_should_reuse_the_runtime_agent_profile_contract()
    {
        ReviewedAgentProfileRelease.Descriptor.FindFieldByName("shadow_profile").MessageType
            .Should().BeSameAs(Aevatar.AI.Abstractions.AgentProfileSnapshot.Descriptor);
        ReviewedAgentProfileRelease.Descriptor.FindFieldByName("enforced_profile").MessageType
            .Should().BeSameAs(Aevatar.AI.Abstractions.AgentProfileSnapshot.Descriptor);
    }

    [Fact]
    public async Task Provision_should_exact_verify_release_and_write_two_immutable_profiles()
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput);

        var exitCode = await commands.ProvisionAsync(
            "access-token",
            releaseSpecPath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(0);
        gateway.PublishCount.Should().Be(4);
        gateway.CreatedSkillsetMembers.Should().Equal(
            "00000000-0000-0000-0000-000000000001@1.2",
            "00000000-0000-0000-0000-000000000002@1.2",
            "00000000-0000-0000-0000-000000000003@1.2",
            "00000000-0000-0000-0000-000000000004@1.2");
        var shadow = ParseProfile(output, AgentProfileRolloutCommands.ShadowProfileFileName);
        var enforced = ParseProfile(output, AgentProfileRolloutCommands.EnforcedProfileFileName);
        shadow.ActivationMode.Should().Be(AgentProfileActivationMode.Shadow);
        enforced.ActivationMode.Should().Be(AgentProfileActivationMode.Enforced);
        shadow.ProfileVersion.Should().NotBe(enforced.ProfileVersion);
        shadow.Members.Should().HaveCount(4);
        shadow.DeterministicPolicySha256.Length.Should().Be(32);
        AgentProfileSnapshotCodec.Verify(shadow).Should().BeTrue();
        AgentProfileSnapshotCodec.Verify(enforced).Should().BeTrue();
        shadow.ToString().Should().NotContain("latest").And.NotContain("placeholder");
    }

    [Fact]
    public async Task Provision_should_use_path_protoc_or_packaged_fallback()
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        using var emptyPath = new TemporaryDirectory();
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalProtoc = Environment.GetEnvironmentVariable("PROTOC");
        var pathProtoc = FindProtocOnPath(originalPath);

        try
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                pathProtoc is null ? emptyPath.Path : System.IO.Path.GetDirectoryName(pathProtoc));
            Environment.SetEnvironmentVariable("PROTOC", null);

            var exitCode = await commands.ProvisionAsync(
                "access-token",
                releaseSpecPath,
                output.Path,
                CancellationToken.None);

            exitCode.Should().Be(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PROTOC", originalProtoc);
        }
    }

    private static string? FindProtocOnPath(string? path)
    {
        var executableName = OperatingSystem.IsWindows() ? "protoc.exe" : "protoc";
        return (path ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => System.IO.Path.Combine(directory, executableName))
            .FirstOrDefault(File.Exists);
    }

    [Fact]
    public async Task Provision_should_exact_verify_existing_artifacts_without_republishing()
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput);
        (await commands.ProvisionAsync("token", releaseSpecPath, output.Path, CancellationToken.None))
            .Should().Be(0);

        var second = await commands.ProvisionAsync("token", releaseSpecPath, output.Path, CancellationToken.None);

        second.Should().Be(0);
        gateway.PublishCount.Should().Be(4);
        gateway.ExactSkillReadCount.Should().Be(8);
        gateway.ExactSkillsetReadCount.Should().Be(2);
    }

    [Fact]
    public async Task Provision_should_reject_existing_profiles_with_different_exact_member_closures()
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput);
        (await commands.ProvisionAsync("token", releaseSpecPath, output.Path, CancellationToken.None))
            .Should().Be(0);
        var enforcedPath = System.IO.Path.Combine(
            output.Path,
            AgentProfileRolloutCommands.EnforcedProfileFileName);
        var enforced = JsonParser.Default.Parse<AgentProfileSnapshot>(File.ReadAllText(enforcedPath));
        enforced.Members[0].SkillRef.Guid = "00000000-0000-0000-0000-000000000099";
        File.WriteAllText(enforcedPath, enforced.ToString());

        var second = await commands.ProvisionAsync("token", releaseSpecPath, output.Path, CancellationToken.None);

        second.Should().Be(1);
    }

    [Fact]
    public async Task Provision_should_reject_existing_profile_with_invalid_policy_hash()
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput);
        (await commands.ProvisionAsync("token", releaseSpecPath, output.Path, CancellationToken.None))
            .Should().Be(0);
        var shadowPath = System.IO.Path.Combine(
            output.Path,
            AgentProfileRolloutCommands.ShadowProfileFileName);
        var shadow = JsonParser.Default.Parse<AgentProfileSnapshot>(File.ReadAllText(shadowPath));
        shadow.DeterministicPolicySha256 = ByteString.CopyFrom(new byte[32]);
        File.WriteAllText(shadowPath, shadow.ToString());

        var second = await commands.ProvisionAsync("token", releaseSpecPath, output.Path, CancellationToken.None);

        second.Should().Be(1);
    }

    [Fact]
    public async Task Provision_should_reject_exact_read_back_guid_that_differs_from_publish_receipt()
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var gateway = FakeGateway.Valid() with { ReturnDifferentExactGuids = true };
        var commands = new AgentProfileRolloutCommands(gateway);
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(1);
        Directory.GetFiles(output.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task Provision_should_fail_closed_on_publisher_mismatch_without_writing_profiles()
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var gateway = FakeGateway.Valid() with { PublisherId = "00000000-0000-0000-0000-000000000099" };
        var commands = new AgentProfileRolloutCommands(gateway);
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(1);
        Directory.GetFiles(output.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task Provision_should_reject_missing_promotion_evidence_before_any_publish()
    {
        using var output = new TemporaryDirectory();
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);

        var exitCode = await commands.ProvisionAsync(
            "token",
            ReleaseSpecPath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(1);
        gateway.PublishCount.Should().Be(0);
        Directory.GetFiles(output.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task Provision_should_reject_non_sha256_evaluation_digest_before_any_publish()
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);
        var releaseSpecPath = await WriteReleaseFixtureAsync(
            releaseInput,
            evaluationReportSha256: "not-a-sha256-digest");

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(1);
        gateway.PublishCount.Should().Be(0);
        Directory.GetFiles(output.Path).Should().BeEmpty();
    }

    [Theory]
    [InlineData(
        "explicit_trigger_aliases: \"connect-service\"",
        "explicit_trigger_aliases: \"service-inventory\"")]
    [InlineData(
        "side_effect_class: AGENT_PROFILE_SIDE_EFFECT_CLASS_MAINTENANCE",
        "side_effect_class: AGENT_PROFILE_SIDE_EFFECT_CLASS_UNSPECIFIED")]
    [InlineData(
        "profile_version: \"nyxid-chat-enforced-v1\"",
        "profile_version: \"nyxid-chat-shadow-v1\"")]
    [InlineData(
        "literal_version: \"1.2\"",
        "literal_version: \"01.2\"")]
    [InlineData(
        "explicit_trigger_aliases: \"connect-service\"",
        "explicit_trigger_aliases: \"SERVICE-INVENTORY\"")]
    [InlineData(
        "allowed_file_paths: \"SKILL.md\"",
        "allowed_file_paths: \"SKILL.md\"\n  allowed_file_paths: \"EXTRA.md\"")]
    public async Task Provision_should_reject_invalid_release_before_any_publish(
        string oldValue,
        string newValue)
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var releasePath = await WriteReleaseFixtureAsync(
            releaseInput,
            releaseText => releaseText.Replace(oldValue, newValue, StringComparison.Ordinal));
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releasePath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(1);
        gateway.PublishCount.Should().Be(0);
        Directory.GetFiles(output.Path).Should().BeEmpty();
    }

    [Fact]
    public void Evaluation_should_reject_inconsistent_case_counts()
    {
        var report = PassingReport();
        report.ExpectedMatchCases = 65;
        report.CorrectSelectionCases = 65;

        var decision = AgentProfileRolloutCommands.Evaluate(report);

        decision.Accepted.Should().BeFalse();
        decision.Violations.Should().Contain("case_counts_are_inconsistent");
    }

    [Fact]
    public void Evaluation_should_reject_missing_online_observation_evidence()
    {
        var report = PassingReport();
        report.EligibleTurnCount = 199;
        report.ContinuousObservationHours = 23.99;

        var decision = AgentProfileRolloutCommands.Evaluate(report);

        decision.Accepted.Should().BeFalse();
        decision.Violations.Should().Contain([
            "eligible_turn_count_below_200",
            "continuous_observation_below_24_hours",
        ]);
    }

    [Fact]
    public void Evaluation_should_reject_negative_latency()
    {
        var report = PassingReport();
        report.ClassifierP95Ms = -1;

        var decision = AgentProfileRolloutCommands.Evaluate(report);

        decision.Accepted.Should().BeFalse();
        decision.Violations.Should().Contain("latency_measurements_must_be_nonnegative");
    }

    [Fact]
    public void Evaluation_should_reject_any_nonzero_safety_invariant()
    {
        var report = PassingReport();
        report.UnsafeAdmissionCount = 1;

        var decision = AgentProfileRolloutCommands.Evaluate(report);

        decision.Accepted.Should().BeFalse();
        decision.Violations.Should().Contain("unsafe_admission_must_be_zero");
    }

    [Fact]
    public void Evaluation_report_should_carry_typed_activation_mode()
    {
        AgentProfileEvaluationReport.Descriptor.FindFieldByName("activation_mode")
            .Should().NotBeNull();
    }

    [Fact]
    public void Evaluation_should_apply_shadow_latency_by_typed_mode_not_profile_name()
    {
        var report = PassingReport();
        report.ProfileVersion = "opaque-profile-revision";
        report.ActivationMode = AgentProfileActivationMode.Shadow;
        report.ClassifierP95Ms = 601;

        var decision = AgentProfileRolloutCommands.Evaluate(report);

        decision.Accepted.Should().BeFalse();
        decision.Violations.Should().Contain("shadow_p95_above_600_ms");
    }

    [Fact]
    public void Cli_should_reject_unknown_options()
    {
        var options = AgentProfileRolloutCliOptions.Parse(
            ["evaluate", "--input", "report.pb.json", "--unknown", "value"]);

        options.IsValid.Should().BeFalse();
    }

    private static string ReleaseSpecPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Profiles", "nyxid-chat", "reviewed-release.textproto");

    private static AgentProfileSnapshot ParseProfile(TemporaryDirectory output, string fileName) =>
        JsonParser.Default.Parse<AgentProfileSnapshot>(File.ReadAllText(System.IO.Path.Combine(output.Path, fileName)));

    private static async Task<string> WriteReleaseFixtureAsync(
        TemporaryDirectory releaseInput,
        Func<string, string>? transform = null,
        string evaluationReportSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")
    {
        var releaseRoot = System.IO.Path.GetDirectoryName(ReleaseSpecPath)!;
        foreach (var sourcePath in Directory.GetFiles(
                     System.IO.Path.Combine(releaseRoot, "packages"),
                     "SKILL.md",
                     SearchOption.AllDirectories))
        {
            var destinationPath = System.IO.Path.Combine(
                releaseInput.Path,
                System.IO.Path.GetRelativePath(releaseRoot, sourcePath));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }

        var releaseText = await File.ReadAllTextAsync(ReleaseSpecPath);
        if (transform is not null)
            releaseText = transform(releaseText);
        releaseText += $"""

security_review_ref: "security-review-fixture"
latency_review_ref: "latency-review-fixture"
evaluation_report_sha256: "{evaluationReportSha256}"
""";
        var releasePath = System.IO.Path.Combine(releaseInput.Path, "reviewed-release.textproto");
        await File.WriteAllTextAsync(releasePath, releaseText);
        return releasePath;
    }

    private static AgentProfileEvaluationReport PassingReport() => new()
    {
        ProfileVersion = "nyxid-chat-shadow-v1",
        ActivationMode = AgentProfileActivationMode.Shadow,
        TotalCases = 64,
        PassedCases = 64,
        ExpectedMatchCases = 60,
        CorrectSelectionCases = 58,
        NoMatchCases = 2,
        ClassifierTimeoutOrErrorCases = 0,
        ClassifierP95Ms = 500,
        TotalPreTurnP95Ms = 550,
        FirstOutputRegressionPercent = 5,
        CompletionRateDropPercentagePoints = 2,
        UnnecessaryToolRoundIncreasePercent = 2,
        EligibleTurnCount = 200,
        ContinuousObservationHours = 24,
    };

    private sealed record FakeGateway : IAgentProfileRolloutOrnnGateway
    {
        private static readonly string[] Names =
        [
            "nyxid-service-discovery",
            "nyxid-service-connect",
            "nyxid-service-call",
            "nyxid-service-maintenance",
        ];
        private static readonly string[][] Tools =
        [
            ["nyxid_service_inventory", "nyxid_catalog", "nyxid_llm_status"],
            ["nyxid_service_inventory", "nyxid_catalog", "nyxid_service_handoff"],
            ["nyxid_service_inventory", "nyxid_service_request"],
            ["nyxid_service_inventory", "nyxid_service_update", "nyxid_service_route", "nyxid_service_delete", "nyxid_service_handoff"],
        ];

        public string PublisherId { get; init; } = "5d0d7b72-acff-49af-bb1b-9f30bbb7c102";
        public bool ReturnDifferentExactGuids { get; init; }
        public int PublishCount { get; private set; }
        public int ExactSkillReadCount { get; private set; }
        public int ExactSkillsetReadCount { get; private set; }
        public IReadOnlyList<string> CreatedSkillsetMembers { get; private set; } = [];

        public static FakeGateway Valid() => new();

        public Task<PublishedOrnnSkill> PublishSkillAsync(string accessToken, byte[] package, CancellationToken ct)
        {
            PublishCount++;
            return Task.FromResult(new PublishedOrnnSkill(GuidFor(PublishCount)));
        }

        public Task<VerifiedOrnnSkillPackage> ReadExactSkillAsync(
            string accessToken,
            string guid,
            string literalVersion,
            CancellationToken ct)
        {
            ExactSkillReadCount++;
            var index = int.Parse(guid[^1..]) - 1;
            return Task.FromResult(new VerifiedOrnnSkillPackage(
                ReturnDifferentExactGuids ? GuidFor(index + 5) : guid,
                Names[index],
                literalVersion,
                PublisherId,
                1024,
                ["SKILL.md"],
                Tools[index]));
        }

        public Task<PublishedOrnnSkillset> CreateSkillsetAsync(
            string accessToken,
            AgentProfileRolloutSkillsetPublishRequest request,
            CancellationToken ct)
        {
            CreatedSkillsetMembers = request.Members;
            return Task.FromResult(new PublishedOrnnSkillset("10000000-0000-0000-0000-000000000000"));
        }

        public Task<VerifiedOrnnSkillset> ReadExactSkillsetAsync(
            string accessToken,
            string guid,
            string literalVersion,
            CancellationToken ct)
        {
            ExactSkillsetReadCount++;
            return Task.FromResult(new VerifiedOrnnSkillset(
                guid,
                "nyxid-chat-core",
                literalVersion,
                PublisherId,
                Enumerable.Range(1, 4)
                    .Select(index => new VerifiedOrnnSkillsetMember(
                        ReturnDifferentExactGuids ? GuidFor(index + 4) : GuidFor(index),
                        "1.2"))
                    .ToArray()));
        }

        private static string GuidFor(int index) => $"00000000-0000-0000-0000-00000000000{index}";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aevatar-rollout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
