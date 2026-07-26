using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
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
    public void Release_spec_should_use_the_shared_pin_only_contract()
    {
        AgentProfileRolloutReleaseSpec.Descriptor.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .Should().Equal(
                "release_id",
                "stage",
                "profile_reference",
                "activation_mode",
                "cohort_salt",
                "cohort_basis_points",
                "expected_published_revision",
                "expected_published_snapshot_sha256",
                "expected_exact_skill_closure",
                "runtime_bounds");
    }

    [Fact]
    public void FormatReleaseSpecUtf8_should_emit_byte_exact_bomless_lf_protojson()
    {
        var actual = AgentProfileRolloutCommands.FormatReleaseSpecUtf8(BuildValidReleaseSpec());
        var expected = File.ReadAllBytes(ReleaseSpecPath);

        actual.Should().Equal(expected);
        actual.Should().NotContain((byte)'\r');
        actual.Should().StartWith((byte)'{');
        actual.Should().EndWith((byte)'\n');
    }

    [Fact]
    public async Task Provision_should_exact_verify_closure_and_write_one_canonical_manifest()
    {
        using var releaseInput = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();
        var outputDirectory = System.IO.Path.Combine(outputParent.Path, "output");
        var release = BuildValidReleaseSpec();
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput, release);

        var exitCode = await commands.ProvisionAsync(
            "access-token",
            releaseSpecPath,
            outputDirectory,
            CancellationToken.None);

        exitCode.Should().Be(0);
        gateway.ReadRequests.Should().Equal(
            release.ExpectedExactSkillClosure.Select(static reference =>
                (reference.SkillGuid, reference.LiteralVersion)));
        Directory.GetFiles(outputDirectory)
            .Should().ContainSingle()
            .Which.Should().EndWith(AgentProfileRolloutCommands.ReleaseSpecFileName);
        var outputPath = System.IO.Path.Combine(
            outputDirectory,
            AgentProfileRolloutCommands.ReleaseSpecFileName);
        var outputJson = await File.ReadAllTextAsync(outputPath);
        outputJson.Should().Be(Format(release));
        JsonParser.Default.Parse<AgentProfileRolloutReleaseSpec>(outputJson)
            .Should().Be(release);
    }

    [Fact]
    public async Task Provision_should_succeed_without_any_runtime_protoc()
    {
        using var releaseInput = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();
        var outputDirectory = System.IO.Path.Combine(outputParent.Path, "output");
        using var emptyPath = new TemporaryDirectory();
        var release = BuildValidReleaseSpec();
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput, release);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalProtoc = Environment.GetEnvironmentVariable("PROTOC");

        try
        {
            Environment.SetEnvironmentVariable("PATH", emptyPath.Path);
            Environment.SetEnvironmentVariable("PROTOC", null);

            var exitCode = await commands.ProvisionAsync(
                "access-token",
                releaseSpecPath,
                outputDirectory,
                CancellationToken.None);

            exitCode.Should().Be(0);
            Directory.GetFiles(outputDirectory)
                .Should().ContainSingle()
                .Which.Should().EndWith(AgentProfileRolloutCommands.ReleaseSpecFileName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PROTOC", originalProtoc);
        }
    }

    [Fact]
    public async Task Provision_should_reject_any_extra_output_entry_before_exact_reads()
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var legacyArtifactPath = System.IO.Path.Combine(
            output.Path,
            "nyxid-chat-shadow-v1.profile.pb.json");
        await File.WriteAllTextAsync(legacyArtifactPath, "legacy Host-owned Profile content");
        var releaseSpecPath = await WriteReleaseFixtureAsync(
            releaseInput,
            BuildValidReleaseSpec());
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(1);
        gateway.ReadRequests.Should().BeEmpty();
        Directory.GetFileSystemEntries(output.Path).Should().Equal(legacyArtifactPath);
    }

    [Fact]
    public async Task Provision_should_reject_output_entry_added_during_exact_reads_before_write()
    {
        using var releaseInput = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();
        var outputDirectory = System.IO.Path.Combine(outputParent.Path, "output");
        var lateArtifactPath = System.IO.Path.Combine(outputDirectory, "late.profile.pb.json");
        var releaseSpecPath = await WriteReleaseFixtureAsync(
            releaseInput,
            BuildValidReleaseSpec());
        var gateway = FakeGateway.Valid() with
        {
            OnFirstRead = () =>
            {
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(lateArtifactPath, "late legacy artifact");
            },
        };
        var commands = new AgentProfileRolloutCommands(gateway);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            outputDirectory,
            CancellationToken.None);

        exitCode.Should().Be(1);
        gateway.ReadRequests.Should().HaveCount(4);
        Directory.GetFileSystemEntries(outputDirectory).Should().Equal(lateArtifactPath);
    }

    [Fact]
    public async Task Provision_should_reject_symlinked_output_root_before_exact_reads()
    {
        using var releaseInput = new TemporaryDirectory();
        using var outputTarget = new TemporaryDirectory();
        using var outputLinkParent = new TemporaryDirectory();
        var outputLink = System.IO.Path.Combine(outputLinkParent.Path, "output-link");
        Directory.CreateSymbolicLink(outputLink, outputTarget.Path);
        var releaseSpecPath = await WriteReleaseFixtureAsync(
            releaseInput,
            BuildValidReleaseSpec());
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            outputLink,
            CancellationToken.None);

        exitCode.Should().Be(1);
        gateway.ReadRequests.Should().BeEmpty();
        Directory.GetFileSystemEntries(outputTarget.Path).Should().BeEmpty();
        Directory.Delete(outputLink);
    }

    [Fact]
    public async Task Provision_should_not_rewrite_an_identical_existing_manifest()
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var release = BuildValidReleaseSpec();
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput, release);
        var outputPath = System.IO.Path.Combine(
            output.Path,
            AgentProfileRolloutCommands.ReleaseSpecFileName);
        await File.WriteAllTextAsync(outputPath, Format(release));
        var originalWriteTime = DateTime.UnixEpoch.AddDays(1);
        File.SetLastWriteTimeUtc(outputPath, originalWriteTime);
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(0);
        gateway.ReadRequests.Should().HaveCount(4);
        File.GetLastWriteTimeUtc(outputPath).Should().Be(originalWriteTime);
        (await File.ReadAllTextAsync(outputPath)).Should().Be(Format(release));
    }

    [Fact]
    public async Task ReadReleaseSpecAsync_should_parse_the_checked_in_canonical_manifest()
    {
        var release = await AgentProfileRolloutCommands.ReadReleaseSpecAsync(
            ReleaseSpecPath,
            CancellationToken.None);

        (await File.ReadAllTextAsync(ReleaseSpecPath)).Should().Be(Format(release));
        release.ProfileReference.OwnerHandle.Should().Be("system");
        release.ProfileReference.ProfileSlug.Should().Be("nyxid-chat");
        release.ExpectedExactSkillClosure.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadReleaseSpecAsync_should_reject_malformed_utf8_bytes()
    {
        using var releaseInput = new TemporaryDirectory();
        var releaseSpecPath = System.IO.Path.Combine(releaseInput.Path, "reviewed-release.json");
        var prefix = Encoding.UTF8.GetBytes("{\"releaseId\":\"");
        var suffix = Encoding.UTF8.GetBytes("\"}");
        var malformedUtf8 = prefix.Concat([(byte)0xC3]).Concat(suffix).ToArray();
        await File.WriteAllBytesAsync(releaseSpecPath, malformedUtf8);

        var act = async () => await AgentProfileRolloutCommands.ReadReleaseSpecAsync(
            releaseSpecPath,
            CancellationToken.None);

        await act.Should().ThrowExactlyAsync<DecoderFallbackException>();
    }

    [Theory]
    [InlineData("{\"releaseId\":")]
    [InlineData("not-json")]
    public async Task Provision_should_reject_malformed_json_before_exact_reads(string malformedJson)
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var releaseSpecPath = await WriteReleaseJsonAsync(releaseInput, malformedJson);
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(1);
        gateway.ReadRequests.Should().BeEmpty();
        Directory.GetFiles(output.Path).Should().BeEmpty();
    }

    [Theory]
    [InlineData("instructions", "host-owned profile content")]
    [InlineData("routingCatalog", "host-owned routing content")]
    [InlineData("unknownField", "must fail closed")]
    public async Task Provision_should_reject_unknown_fields_before_exact_reads(
        string fieldName,
        string fieldValue)
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var release = BuildValidReleaseSpec();
        var validJson = Format(release);
        var jsonWithUnknownField = $"{{\"{fieldName}\":\"{fieldValue}\"," + validJson[1..];
        var releaseSpecPath = await WriteReleaseJsonAsync(releaseInput, jsonWithUnknownField);
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(1);
        gateway.ReadRequests.Should().BeEmpty();
        Directory.GetFiles(output.Path).Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing-profile-reference")]
    [InlineData("wrong-profile-reference")]
    [InlineData("unspecified-activation")]
    [InlineData("invalid-cohort")]
    [InlineData("missing-revision")]
    [InlineData("missing-snapshot-digest")]
    [InlineData("missing-closure")]
    [InlineData("duplicate-closure")]
    [InlineData("noncanonical-closure-order")]
    [InlineData("invalid-runtime-bounds")]
    public async Task Provision_should_reject_invalid_admission_pins_before_exact_reads(
        string invalidCase)
    {
        using var releaseInput = new TemporaryDirectory();
        using var output = new TemporaryDirectory();
        var release = BuildValidReleaseSpec();
        MakeInvalid(release, invalidCase);
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput, release);
        var gateway = FakeGateway.Valid();
        var commands = new AgentProfileRolloutCommands(gateway);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            output.Path,
            CancellationToken.None);

        exitCode.Should().Be(1);
        gateway.ReadRequests.Should().BeEmpty();
        Directory.GetFiles(output.Path).Should().BeEmpty();
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("literal-version")]
    [InlineData("name")]
    [InlineData("publisher")]
    public async Task Provision_should_fail_closed_on_exact_read_back_mismatch(
        string mismatchField)
    {
        using var releaseInput = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();
        var outputDirectory = System.IO.Path.Combine(outputParent.Path, "output");
        var release = BuildValidReleaseSpec();
        var releaseSpecPath = await WriteReleaseFixtureAsync(releaseInput, release);
        var gateway = FakeGateway.Valid() with { MismatchField = mismatchField };
        var commands = new AgentProfileRolloutCommands(gateway);

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            outputDirectory,
            CancellationToken.None);

        exitCode.Should().Be(1);
        gateway.ReadRequests.Should().ContainSingle();
        Directory.Exists(outputDirectory).Should().BeFalse();
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
        System.IO.Path.Combine(AppContext.BaseDirectory, "Profiles", "nyxid-chat", "reviewed-release.json");

    private static AgentProfileRolloutReleaseSpec BuildValidReleaseSpec()
    {
        var release = new AgentProfileRolloutReleaseSpec
        {
            ReleaseId = "nyxid-chat-core-1.2",
            Stage = "shadow-canary",
            ProfileReference = new AgentProfileReference
            {
                OwnerHandle = "system",
                ProfileSlug = "nyxid-chat",
            },
            ActivationMode = AgentProfileRolloutActivationMode.Shadow,
            CohortSalt = "nyxid-chat-core-1.2-shadow-canary",
            CohortBasisPoints = 500,
            ExpectedPublishedRevision = 17,
            ExpectedPublishedSnapshotSha256 = ByteString.CopyFrom(
                Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray()),
            RuntimeBounds = new AgentProfileRolloutRuntimeBounds
            {
                MaxPlanSteps = 4,
                HandoffTtlSeconds = 900,
                ClassifierTimeoutMs = 600,
                MaxSelectedSkillBytes = 24_576,
            },
        };
        release.ExpectedExactSkillClosure.Add(
            Skill("00000000-0000-0000-0000-000000000001", "nyxid-service-discovery"));
        release.ExpectedExactSkillClosure.Add(
            Skill("00000000-0000-0000-0000-000000000002", "nyxid-service-connect"));
        release.ExpectedExactSkillClosure.Add(
            Skill("00000000-0000-0000-0000-000000000003", "nyxid-service-call"));
        release.ExpectedExactSkillClosure.Add(
            Skill("00000000-0000-0000-0000-000000000004", "nyxid-service-maintenance"));
        return release;
    }

    private static ExactOrnnSkillReference Skill(string guid, string name) => new()
    {
        SkillGuid = guid,
        LiteralVersion = "1.2",
        ExpectedName = name,
        ExpectedPublisherId = "5d0d7b72-acff-49af-bb1b-9f30bbb7c102",
    };

    private static void MakeInvalid(AgentProfileRolloutReleaseSpec release, string invalidCase)
    {
        switch (invalidCase)
        {
            case "missing-profile-reference":
                release.ProfileReference = null;
                break;
            case "wrong-profile-reference":
                release.ProfileReference.ProfileSlug = "other-profile";
                break;
            case "unspecified-activation":
                release.ActivationMode = AgentProfileRolloutActivationMode.Unspecified;
                break;
            case "invalid-cohort":
                release.CohortBasisPoints = 0;
                break;
            case "missing-revision":
                release.ExpectedPublishedRevision = 0;
                break;
            case "missing-snapshot-digest":
                release.ExpectedPublishedSnapshotSha256 = ByteString.Empty;
                break;
            case "missing-closure":
                release.ExpectedExactSkillClosure.Clear();
                break;
            case "duplicate-closure":
                release.ExpectedExactSkillClosure.Add(release.ExpectedExactSkillClosure[0].Clone());
                break;
            case "noncanonical-closure-order":
                var first = release.ExpectedExactSkillClosure[0].Clone();
                release.ExpectedExactSkillClosure[0] = release.ExpectedExactSkillClosure[1].Clone();
                release.ExpectedExactSkillClosure[1] = first;
                break;
            case "invalid-runtime-bounds":
                release.RuntimeBounds.ClassifierTimeoutMs = 601;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidCase), invalidCase, null);
        }
    }

    private static async Task<string> WriteReleaseFixtureAsync(
        TemporaryDirectory releaseInput,
        AgentProfileRolloutReleaseSpec release) =>
        await WriteReleaseJsonAsync(releaseInput, Format(release));

    private static async Task<string> WriteReleaseJsonAsync(
        TemporaryDirectory releaseInput,
        string json)
    {
        var releasePath = System.IO.Path.Combine(releaseInput.Path, "reviewed-release.json");
        await File.WriteAllTextAsync(releasePath, json);
        return releasePath;
    }

    private static string Format(AgentProfileRolloutReleaseSpec release) =>
        Encoding.UTF8.GetString(AgentProfileRolloutCommands.FormatReleaseSpecUtf8(release));

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

        public string? MismatchField { get; init; }
        public Action? OnFirstRead { get; init; }
        public List<(string Guid, string LiteralVersion)> ReadRequests { get; } = [];

        public static FakeGateway Valid() => new();

        public Task<VerifiedExactOrnnSkill> ReadExactSkillAsync(
            string accessToken,
            string guid,
            string literalVersion,
            CancellationToken ct)
        {
            ReadRequests.Add((guid, literalVersion));
            if (ReadRequests.Count == 1)
                OnFirstRead?.Invoke();
            var index = int.Parse(guid[^1..]) - 1;
            return Task.FromResult(new VerifiedExactOrnnSkill(
                MismatchField == "guid"
                    ? "00000000-0000-0000-0000-000000000099"
                    : guid,
                MismatchField == "name" ? "different-name" : Names[index],
                MismatchField == "literal-version" ? "9.9" : literalVersion,
                MismatchField == "publisher"
                    ? "00000000-0000-0000-0000-000000000099"
                    : "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aevatar-rollout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
