using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.Tools.AgentProfileRollout.Contracts;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Tools.AgentProfileRollout;

public sealed class AgentProfileRolloutCommands
{
    public const string ShadowProfileFileName = "nyxid-chat-shadow-v1.profile.pb.json";
    public const string EnforcedProfileFileName = "nyxid-chat-enforced-v1.profile.pb.json";
    private static readonly JsonFormatter ProfileFormatter = new(
        JsonFormatter.Settings.Default.WithIndentation("  "));
    private readonly IAgentProfileRolloutOrnnGateway _ornnGateway;
    private readonly ILogger _logger;

    public AgentProfileRolloutCommands(
        IAgentProfileRolloutOrnnGateway ornnGateway,
        ILogger<AgentProfileRolloutCommands>? logger = null)
    {
        _ornnGateway = ornnGateway ?? throw new ArgumentNullException(nameof(ornnGateway));
        _logger = logger ?? NullLogger<AgentProfileRolloutCommands>.Instance;
    }

    public async Task<int> ProvisionAsync(
        string accessToken,
        string releaseSpecPath,
        string outputDirectory,
        CancellationToken ct)
    {
        try
        {
            var release = await ReviewedReleaseTextProto.LoadAsync(releaseSpecPath, ct);
            ValidateReviewedRelease(release);
            Directory.CreateDirectory(outputDirectory);

            var shadowPath = Path.Combine(outputDirectory, ShadowProfileFileName);
            var enforcedPath = Path.Combine(outputDirectory, EnforcedProfileFileName);
            var shadowExists = File.Exists(shadowPath);
            var enforcedExists = File.Exists(enforcedPath);
            if (shadowExists != enforcedExists)
                throw new InvalidOperationException("Resolved profile artifacts must exist as a complete SHADOW/ENFORCED pair.");

            if (shadowExists)
            {
                var existingShadow = ParseProfile(await File.ReadAllTextAsync(shadowPath, ct));
                var existingEnforced = ParseProfile(await File.ReadAllTextAsync(enforcedPath, ct));
                ValidateExistingProfiles(release, existingShadow, existingEnforced);
                await VerifyResolvedProfilesAsync(accessToken, release, existingShadow, ct);
                return 0;
            }

            var releaseDirectory = Path.GetDirectoryName(Path.GetFullPath(releaseSpecPath))!;
            var verifiedPackages = new List<VerifiedOrnnSkillPackage>(release.Packages.Count);
            foreach (var reviewedPackage in release.Packages)
            {
                var packageBytes = BuildPackageArchive(releaseDirectory, reviewedPackage);
                var published = await _ornnGateway.PublishSkillAsync(accessToken, packageBytes, ct);
                RequireCanonicalGuid(published.Guid, "published skill guid");
                var verified = await _ornnGateway.ReadExactSkillAsync(
                    accessToken,
                    published.Guid,
                    reviewedPackage.LiteralVersion,
                    ct);
                VerifyPackage(reviewedPackage, release.ReviewedPublisherId, published.Guid, verified);
                verifiedPackages.Add(verified);
            }

            var skillset = await _ornnGateway.CreateSkillsetAsync(
                accessToken,
                new AgentProfileRolloutSkillsetPublishRequest(
                    release.SkillsetName,
                    "Reviewed immutable NyxID chat service capability set.",
                    "Select exactly one focused member from the bound agent profile; member bodies never grant tools.",
                    release.SkillsetLiteralVersion,
                    verifiedPackages
                        .Select(static package => $"{package.Guid}@{package.LiteralVersion}")
                        .ToArray()),
                ct);
            RequireCanonicalGuid(skillset.Guid, "published skillset guid");
            var exactSkillset = await _ornnGateway.ReadExactSkillsetAsync(
                accessToken,
                skillset.Guid,
                release.SkillsetLiteralVersion,
                ct);
            VerifySkillset(release, skillset.Guid, exactSkillset, verifiedPackages);

            var shadow = MaterializeProfile(release.ShadowProfile, release, exactSkillset, verifiedPackages);
            var enforced = MaterializeProfile(release.EnforcedProfile, release, exactSkillset, verifiedPackages);
            ValidateMaterializedProfiles(shadow, enforced);
            await WriteProfilesAtomicallyAsync(shadowPath, enforcedPath, shadow, enforced, ct);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Agent profile provisioning failed closed");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static AgentProfileEvaluationDecision Evaluate(AgentProfileEvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var violations = new List<string>();
        if (string.IsNullOrWhiteSpace(report.ProfileVersion))
            violations.Add("profile_version_must_not_be_empty");
        if (report.TotalCases < 0 ||
            report.PassedCases < 0 || report.PassedCases > report.TotalCases ||
            report.ExpectedMatchCases < 0 || report.ExpectedMatchCases > report.TotalCases ||
            report.CorrectSelectionCases < 0 || report.CorrectSelectionCases > report.ExpectedMatchCases ||
            report.NoMatchCases < 0 || report.NoMatchCases > report.ExpectedMatchCases ||
            report.CorrectSelectionCases + report.NoMatchCases > report.ExpectedMatchCases ||
            report.ClassifierTimeoutOrErrorCases < 0 ||
            report.ClassifierTimeoutOrErrorCases > report.TotalCases)
        {
            violations.Add("case_counts_are_inconsistent");
        }
        if (report.TotalCases != 64 || report.PassedCases != 64)
            violations.Add("offline_invariants_must_pass_64_of_64");
        if (report.ExpectedMatchCases <= 0 ||
            Percentage(report.CorrectSelectionCases, report.ExpectedMatchCases) < 95)
        {
            violations.Add("selection_accuracy_below_95_percent");
        }
        if (report.ExpectedMatchCases <= 0 || Percentage(report.NoMatchCases, report.ExpectedMatchCases) > 5)
            violations.Add("expected_match_no_match_rate_above_5_percent");
        if (report.TotalCases <= 0 || Percentage(report.ClassifierTimeoutOrErrorCases, report.TotalCases) > 1)
            violations.Add("classifier_timeout_or_error_rate_above_1_percent");

        AddZeroInvariant(violations, report.UnsafeAdmissionCount, "unsafe_admission");
        AddZeroInvariant(violations, report.ApprovalBypassCount, "approval_bypass");
        AddZeroInvariant(violations, report.ReplayAcceptanceCount, "replay_acceptance");
        AddZeroInvariant(violations, report.SecretTelemetryViolationCount, "secret_telemetry_violation");
        AddZeroInvariant(violations, report.ShadowExecutionSideEffectCount, "shadow_execution_side_effect");

        if (report.ActivationMode == AgentProfileActivationMode.Unspecified)
            violations.Add("activation_mode_must_be_typed");
        if (!IsNonnegativeFinite(report.ClassifierP95Ms) ||
            !IsNonnegativeFinite(report.TotalPreTurnP95Ms))
        {
            violations.Add("latency_measurements_must_be_nonnegative");
        }
        if (!double.IsFinite(report.FirstOutputRegressionPercent) ||
            !double.IsFinite(report.CompletionRateDropPercentagePoints) ||
            !double.IsFinite(report.UnnecessaryToolRoundIncreasePercent))
        {
            violations.Add("quality_measurements_must_be_finite");
        }
        if (report.ActivationMode == AgentProfileActivationMode.Shadow &&
            (report.ClassifierP95Ms > 600 || report.TotalPreTurnP95Ms > 600))
        {
            violations.Add("shadow_p95_above_600_ms");
        }
        if (report.ActivationMode == AgentProfileActivationMode.Enforced &&
            report.TotalPreTurnP95Ms > 2100)
        {
            violations.Add("enforced_pre_turn_p95_above_2100_ms");
        }
        if (report.FirstOutputRegressionPercent > 10)
            violations.Add("first_output_regression_above_10_percent");
        if (report.CompletionRateDropPercentagePoints > 5)
            violations.Add("completion_rate_drop_above_5_points");
        if (report.UnnecessaryToolRoundIncreasePercent > 5)
            violations.Add("unnecessary_tool_round_increase_above_5_percent");
        if (report.EligibleTurnCount < 200)
            violations.Add("eligible_turn_count_below_200");
        if (!double.IsFinite(report.ContinuousObservationHours) ||
            report.ContinuousObservationHours < 24)
        {
            violations.Add("continuous_observation_below_24_hours");
        }

        var decision = new AgentProfileEvaluationDecision { Accepted = violations.Count == 0 };
        decision.Violations.AddRange(violations);
        return decision;
    }

    public static async Task<int> EvaluateFileAsync(string path, CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(path, ct);
        var report = JsonParser.Default.Parse<AgentProfileEvaluationReport>(json);
        var decision = Evaluate(report);
        Console.WriteLine(ProfileFormatter.Format(decision));
        return decision.Accepted ? 0 : 1;
    }

    public static void ValidateReviewedRelease(ReviewedAgentProfileRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        RequireNonEmpty(release.ReleaseId, "release_id");
        RequireCanonicalGuid(release.ReviewedPublisherId, "reviewed_publisher_id");
        RequireNonEmpty(release.SkillsetName, "skillset_name");
        RequireLiteralVersion(release.SkillsetLiteralVersion, "skillset_literal_version");
        RequireReviewReference(release.SecurityReviewRef, "security_review_ref");
        RequireReviewReference(release.LatencyReviewRef, "latency_review_ref");
        RequireSha256(release.EvaluationReportSha256, "evaluation_report_sha256");
        if (release.Packages.Count != 4)
            throw new InvalidOperationException("A reviewed nyxid-chat release must contain exactly four packages.");
        if (release.Packages.Select(static package => package.ExpectedName).Distinct(StringComparer.Ordinal).Count() != 4 ||
            release.Packages.Select(static package => package.IntentId).Distinct(StringComparer.Ordinal).Count() != 4)
        {
            throw new InvalidOperationException("Reviewed package names and intent IDs must be unique.");
        }

        var triggerAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in release.Packages)
        {
            RequireNonEmpty(package.ExpectedName, "expected_name");
            RequireLiteralVersion(package.LiteralVersion, "literal_version");
            RequireNonEmpty(package.PackageRelativePath, "package_relative_path");
            RequireNonEmpty(package.IntentId, "intent_id");
            RequireNonEmpty(package.RoutingDescription, "routing_description");
            if (package.MaxPackageBytes is <= 0 or > 65536)
                throw new InvalidOperationException($"Package '{package.ExpectedName}' has an invalid byte bound.");
            var taskToolPolicy = package.TaskToolPolicy;
            if (package.AllowedFilePaths.Count != 1 ||
                !string.Equals(package.AllowedFilePaths[0], "SKILL.md", StringComparison.Ordinal) ||
                taskToolPolicy is null || taskToolPolicy.ToolNames.Count == 0)
                throw new InvalidOperationException($"Package '{package.ExpectedName}' requires files and a narrow task policy.");
            RejectLegacyOrCredentialNames(taskToolPolicy.ToolNames);
            if (package.SideEffectClass is not (
                    AgentProfileSideEffectClass.ReadOnly or
                    AgentProfileSideEffectClass.ExternalHandoff or
                    AgentProfileSideEffectClass.ServiceCall or
                    AgentProfileSideEffectClass.Maintenance))
            {
                throw new InvalidOperationException($"Package '{package.ExpectedName}' requires a typed side-effect class.");
            }
            foreach (var alias in package.ExplicitTriggerAliases)
            {
                RequireNonEmpty(alias, "explicit_trigger_alias");
                if (!triggerAliases.Add(alias))
                    throw new InvalidOperationException($"Trigger alias '{alias}' must be unique across the reviewed release.");
            }
        }

        ValidateProfileTemplate(release.ShadowProfile, AgentProfileActivationMode.Shadow);
        ValidateProfileTemplate(release.EnforcedProfile, AgentProfileActivationMode.Enforced);
        ValidateProfilePair(release.ShadowProfile, release.EnforcedProfile);
    }

    private async Task VerifyResolvedProfilesAsync(
        string accessToken,
        ReviewedAgentProfileRelease release,
        AgentProfileSnapshot profile,
        CancellationToken ct)
    {
        var verifiedPackages = new List<VerifiedOrnnSkillPackage>(profile.Members.Count);
        foreach (var member in profile.Members)
        {
            var verified = await _ornnGateway.ReadExactSkillAsync(
                accessToken,
                member.SkillRef.Guid,
                member.SkillRef.LiteralVersion,
                ct);
            var reviewed = release.Packages.Single(package => package.ExpectedName == member.ExpectedSkillName);
            VerifyPackage(reviewed, release.ReviewedPublisherId, member.SkillRef.Guid, verified);
            verifiedPackages.Add(verified);
        }

        var skillset = await _ornnGateway.ReadExactSkillsetAsync(
            accessToken,
            profile.SkillsetProvenance.Guid,
            profile.SkillsetProvenance.LiteralVersion,
            ct);
        VerifySkillset(release, profile.SkillsetProvenance.Guid, skillset, verifiedPackages);
    }

    private static void VerifyPackage(
        ReviewedSkillPackage reviewed,
        string reviewedPublisherId,
        string expectedGuid,
        VerifiedOrnnSkillPackage actual)
    {
        if (!string.Equals(actual.Guid, expectedGuid, StringComparison.Ordinal) ||
            !string.Equals(actual.Name, reviewed.ExpectedName, StringComparison.Ordinal) ||
            !string.Equals(actual.LiteralVersion, reviewed.LiteralVersion, StringComparison.Ordinal) ||
            !string.Equals(actual.PublisherId, reviewedPublisherId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Exact package identity mismatch for '{reviewed.ExpectedName}'.");
        }
        if (actual.PackageBytes <= 0 || actual.PackageBytes > reviewed.MaxPackageBytes)
            throw new InvalidOperationException($"Package '{reviewed.ExpectedName}' exceeds its reviewed byte bound.");
        if (!SetEquals(actual.FilePaths, reviewed.AllowedFilePaths) ||
            !SetEquals(actual.DeclaredToolNames, reviewed.TaskToolPolicy.ToolNames))
        {
            throw new InvalidOperationException($"Package '{reviewed.ExpectedName}' file or tool declaration mismatch.");
        }
        RejectLegacyOrCredentialNames(actual.DeclaredToolNames);
    }

    private static void VerifySkillset(
        ReviewedAgentProfileRelease release,
        string expectedGuid,
        VerifiedOrnnSkillset skillset,
        IReadOnlyList<VerifiedOrnnSkillPackage> packages)
    {
        if (!string.Equals(skillset.Guid, expectedGuid, StringComparison.Ordinal) ||
            !string.Equals(skillset.Name, release.SkillsetName, StringComparison.Ordinal) ||
            !string.Equals(skillset.LiteralVersion, release.SkillsetLiteralVersion, StringComparison.Ordinal) ||
            !string.Equals(skillset.PublisherId, release.ReviewedPublisherId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Exact skillset identity mismatch.");
        }

        var expected = packages.Select(static package => (package.Guid, package.LiteralVersion)).ToHashSet();
        var actual = skillset.Closure.Select(static member => (member.Guid, member.LiteralVersion)).ToHashSet();
        foreach (var member in skillset.Closure)
        {
            RequireCanonicalGuid(member.Guid, "skillset closure member guid");
            RequireLiteralVersion(member.LiteralVersion, "skillset closure member version");
        }
        if (expected.Count != 4 || actual.Count != 4 || skillset.Closure.Count != 4 || !actual.SetEquals(expected))
            throw new InvalidOperationException("Exact skillset closure must equal the four reviewed members.");
    }

    private static AgentProfileSnapshot MaterializeProfile(
        AgentProfileSnapshot template,
        ReviewedAgentProfileRelease release,
        VerifiedOrnnSkillset skillset,
        IReadOnlyList<VerifiedOrnnSkillPackage> packages)
    {
        var profile = template.Clone();
        profile.SkillsetProvenance = new ExactRemoteSkillsetRef
        {
            Guid = skillset.Guid,
            LiteralVersion = skillset.LiteralVersion,
        };
        profile.Members.Clear();
        foreach (var reviewed in release.Packages)
        {
            var verified = packages.Single(package => package.Name == reviewed.ExpectedName);
            var member = new AgentProfileSkillMember
            {
                IntentId = reviewed.IntentId,
                RoutingDescription = reviewed.RoutingDescription,
                SkillRef = new ExactRemoteSkillRef
                {
                    Guid = verified.Guid,
                    LiteralVersion = verified.LiteralVersion,
                },
                TaskToolPolicy = reviewed.TaskToolPolicy.Clone(),
                SideEffectClass = reviewed.SideEffectClass,
                ExpectedSkillName = reviewed.ExpectedName,
                ReviewedPublisherId = release.ReviewedPublisherId,
            };
            member.ExplicitTriggerAliases.AddRange(reviewed.ExplicitTriggerAliases);
            profile.Members.Add(member);
        }

        profile.DeterministicPolicySha256 = ByteString.Empty;
        return AgentProfileSnapshotCodec.Seal(profile);
    }

    private static void ValidateExistingProfiles(
        ReviewedAgentProfileRelease release,
        AgentProfileSnapshot shadow,
        AgentProfileSnapshot enforced)
    {
        ValidateMaterializedProfiles(shadow, enforced);
        if (!StaticProfileEquals(release.ShadowProfile, shadow) ||
            !StaticProfileEquals(release.EnforcedProfile, enforced))
        {
            throw new InvalidOperationException("Existing resolved profiles do not match the reviewed release input.");
        }
    }

    private static bool StaticProfileEquals(AgentProfileSnapshot template, AgentProfileSnapshot actual)
    {
        var copy = actual.Clone();
        copy.SkillsetProvenance = null;
        copy.Members.Clear();
        copy.DeterministicPolicySha256 = ByteString.Empty;
        return copy.Equals(template);
    }

    private static void ValidateMaterializedProfiles(
        AgentProfileSnapshot shadow,
        AgentProfileSnapshot enforced)
    {
        ValidateProfileTemplate(shadow, AgentProfileActivationMode.Shadow, requireResolvedReferences: true);
        ValidateProfileTemplate(enforced, AgentProfileActivationMode.Enforced, requireResolvedReferences: true);
        ValidateProfilePair(shadow, enforced);
        if (!shadow.SkillsetProvenance.Equals(enforced.SkillsetProvenance) ||
            !shadow.Members.SequenceEqual(enforced.Members) ||
            shadow.Members.Count != 4 ||
            enforced.Members.Count != 4)
        {
            throw new InvalidOperationException("SHADOW and ENFORCED must be distinct complete immutable profiles over one closure.");
        }
    }

    private static void ValidateProfilePair(
        AgentProfileSnapshot shadow,
        AgentProfileSnapshot enforced)
    {
        if (shadow.ProfileVersion == enforced.ProfileVersion)
            throw new InvalidOperationException("SHADOW and ENFORCED profile versions must be distinct.");

        var normalizedEnforced = enforced.Clone();
        normalizedEnforced.ProfileVersion = shadow.ProfileVersion;
        normalizedEnforced.PolicyRevision = shadow.PolicyRevision;
        normalizedEnforced.ActivationMode = shadow.ActivationMode;
        normalizedEnforced.DeterministicPolicySha256 = shadow.DeterministicPolicySha256;
        if (!normalizedEnforced.Equals(shadow))
            throw new InvalidOperationException("SHADOW and ENFORCED profiles must share one reviewed policy and closure.");
    }

    private static void ValidateProfileTemplate(
        AgentProfileSnapshot? profile,
        AgentProfileActivationMode mode,
        bool requireResolvedReferences = false)
    {
        if (profile is null)
            throw new InvalidOperationException($"Missing {mode} profile template.");
        RequireNonEmpty(profile.ProfileId, "profile_id");
        RequireNonEmpty(profile.ProfileVersion, "profile_version");
        RequireNonEmpty(profile.AgentKind, "agent_kind");
        RequireNonEmpty(profile.PolicyRevision, "policy_revision");
        RequireNonEmpty(profile.RouteToolSetRef, "route_tool_set_ref");
        if (profile.ActivationMode != mode)
            throw new InvalidOperationException($"Profile '{profile.ProfileVersion}' has the wrong activation mode.");
        if (profile.MaxPlanSteps != 4 || profile.HandoffTtlSeconds != 900 ||
            profile.ClassifierTimeoutMs != 600 || profile.ExactSkillFetchTimeoutMs != 1500 ||
            profile.MaxSelectedSkillBytes != 24576)
        {
            throw new InvalidOperationException($"Profile '{profile.ProfileVersion}' does not match the reviewed v1 bounds.");
        }
        RejectLegacyOrCredentialNames(profile.MaximumToolPolicy.ToolNames);
        RejectLegacyOrCredentialNames(profile.RecoveryToolPolicy.ToolNames);
        RequireSubset(profile.RecoveryToolPolicy.ToolNames, profile.MaximumToolPolicy.ToolNames, "recovery tools");
        RequireSubset(profile.RecoveryToolPolicy.ToolSetRefs, profile.MaximumToolPolicy.ToolSetRefs, "recovery tool sets");
        if (!requireResolvedReferences)
            return;

        RequireCanonicalGuid(profile.SkillsetProvenance.Guid, "skillset guid");
        RequireLiteralVersion(profile.SkillsetProvenance.LiteralVersion, "skillset version");
        if (profile.DeterministicPolicySha256.Length != 32)
            throw new InvalidOperationException("Profile policy hash must be SHA-256.");
        if (!AgentProfileSnapshotCodec.Verify(profile))
        {
            throw new InvalidOperationException("Profile policy hash does not match the immutable profile content.");
        }
        foreach (var member in profile.Members)
        {
            RequireCanonicalGuid(member.SkillRef.Guid, "member guid");
            RequireLiteralVersion(member.SkillRef.LiteralVersion, "member version");
            RequireSubset(member.TaskToolPolicy.ToolNames, profile.MaximumToolPolicy.ToolNames, "member tools");
            RequireSubset(member.TaskToolPolicy.ToolSetRefs, profile.MaximumToolPolicy.ToolSetRefs, "member tool sets");
        }
    }

    private static async Task WriteProfilesAtomicallyAsync(
        string shadowPath,
        string enforcedPath,
        AgentProfileSnapshot shadow,
        AgentProfileSnapshot enforced,
        CancellationToken ct)
    {
        var transactionId = Guid.NewGuid().ToString("N");
        var shadowTemp = $"{shadowPath}.{transactionId}.tmp";
        var enforcedTemp = $"{enforcedPath}.{transactionId}.tmp";
        var shadowCommitted = false;
        var enforcedCommitted = false;
        try
        {
            await File.WriteAllTextAsync(shadowTemp, ProfileFormatter.Format(shadow) + Environment.NewLine, ct);
            await File.WriteAllTextAsync(enforcedTemp, ProfileFormatter.Format(enforced) + Environment.NewLine, ct);
            _ = ParseProfile(await File.ReadAllTextAsync(shadowTemp, ct));
            _ = ParseProfile(await File.ReadAllTextAsync(enforcedTemp, ct));
            File.Move(shadowTemp, shadowPath);
            shadowCommitted = true;
            File.Move(enforcedTemp, enforcedPath);
            enforcedCommitted = true;
        }
        catch
        {
            if (shadowCommitted)
                File.Delete(shadowPath);
            if (enforcedCommitted)
                File.Delete(enforcedPath);
            throw;
        }
        finally
        {
            File.Delete(shadowTemp);
            File.Delete(enforcedTemp);
        }
    }

    private static AgentProfileSnapshot ParseProfile(string json) =>
        JsonParser.Default.Parse<AgentProfileSnapshot>(json);

    private static byte[] BuildPackageArchive(string releaseDirectory, ReviewedSkillPackage package)
    {
        var sourcePath = Path.GetFullPath(Path.Combine(releaseDirectory, package.PackageRelativePath));
        if (!sourcePath.StartsWith(releaseDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(sourcePath))
        {
            throw new InvalidOperationException($"Reviewed package path is missing or escapes the release root: {package.PackageRelativePath}");
        }

        var content = File.ReadAllText(sourcePath);
        var packageBytes = Encoding.UTF8.GetByteCount("SKILL.md") + Encoding.UTF8.GetByteCount(content);
        if (packageBytes <= 0 || packageBytes > package.MaxPackageBytes)
            throw new InvalidOperationException($"Package '{package.ExpectedName}' exceeds its reviewed byte bound.");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry($"{package.ExpectedName}/SKILL.md", CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return stream.ToArray();
    }

    private static void RejectLegacyOrCredentialNames(IEnumerable<string> names)
    {
        var forbidden = names.Where(static name =>
            name is "nyxid_services" or "nyxid_proxy" or "nyxid_external_keys" ||
            name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("api_key", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (forbidden.Length > 0)
            throw new InvalidOperationException($"Reviewed policy contains forbidden broad or credential-bearing names: {string.Join(", ", forbidden)}");
    }

    private static void RequireSubset(IEnumerable<string> subset, IEnumerable<string> superset, string label)
    {
        var maximum = superset.ToHashSet(StringComparer.Ordinal);
        if (subset.Any(item => !maximum.Contains(item)))
            throw new InvalidOperationException($"{label} must be a strict profile-policy subset.");
    }

    private static bool SetEquals(IEnumerable<string> left, IEnumerable<string> right) =>
        left.ToHashSet(StringComparer.Ordinal).SetEquals(right);

    private static void AddZeroInvariant(List<string> violations, int value, string name)
    {
        if (value != 0)
            violations.Add($"{name}_must_be_zero");
    }

    private static double Percentage(int numerator, int denominator) => denominator == 0 ? 100 : numerator * 100d / denominator;

    private static bool IsNonnegativeFinite(double value) => double.IsFinite(value) && value >= 0;

    private static void RequireNonEmpty(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} must not be empty.");
    }

    private static void RequireCanonicalGuid(string? value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name} must be a canonical GUID.");
    }

    private static void RequireLiteralVersion(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Split('.') is not [var major, var minor] ||
            !int.TryParse(major, out var majorValue) || !int.TryParse(minor, out var minorValue) ||
            majorValue < 0 || minorValue < 0 ||
            !string.Equals(majorValue.ToString(), major, StringComparison.Ordinal) ||
            !string.Equals(minorValue.ToString(), minor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name} must be a literal major.minor version.");
        }
    }

    private static void RequireReviewReference(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} must not be empty.");
        if (value.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("pending", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("todo", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{name} must identify a completed review.");
        }
    }

    private static void RequireSha256(string? value, string name)
    {
        if (value is null ||
            value.Length != 64 ||
            value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException($"{name} must be a lowercase SHA-256 digest.");
        }
    }
}

public interface IAgentProfileRolloutOrnnGateway
{
    Task<PublishedOrnnSkill> PublishSkillAsync(string accessToken, byte[] package, CancellationToken ct);
    Task<VerifiedOrnnSkillPackage> ReadExactSkillAsync(string accessToken, string guid, string literalVersion, CancellationToken ct);
    Task<PublishedOrnnSkillset> CreateSkillsetAsync(string accessToken, AgentProfileRolloutSkillsetPublishRequest request, CancellationToken ct);
    Task<VerifiedOrnnSkillset> ReadExactSkillsetAsync(string accessToken, string guid, string literalVersion, CancellationToken ct);
}

public sealed record PublishedOrnnSkill(string Guid);
public sealed record PublishedOrnnSkillset(string Guid);
public sealed record VerifiedOrnnSkillPackage(
    string Guid,
    string Name,
    string LiteralVersion,
    string PublisherId,
    int PackageBytes,
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<string> DeclaredToolNames);
public sealed record VerifiedOrnnSkillsetMember(string Guid, string LiteralVersion);
public sealed record VerifiedOrnnSkillset(
    string Guid,
    string Name,
    string LiteralVersion,
    string PublisherId,
    IReadOnlyList<VerifiedOrnnSkillsetMember> Closure);
public sealed record AgentProfileRolloutSkillsetPublishRequest(
    string Name,
    string Description,
    string Instructions,
    string LiteralVersion,
    IReadOnlyList<string> Members);

public sealed class OrnnAgentProfileRolloutGateway(OrnnSkillClient client) : IAgentProfileRolloutOrnnGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<PublishedOrnnSkill> PublishSkillAsync(string accessToken, byte[] package, CancellationToken ct)
    {
        var response = await client.PublishSkillAsync(accessToken, package, ct);
        if (!response.Succeeded)
            throw new InvalidOperationException(response.Error ?? "Ornn skill publish failed.");
        using var document = JsonDocument.Parse(response.RawResponse);
        var data = document.RootElement.GetProperty("data");
        var guid = data.GetProperty("guid").GetString();
        return new PublishedOrnnSkill(guid ?? throw new InvalidOperationException("Ornn skill publish returned no GUID."));
    }

    public async Task<VerifiedOrnnSkillPackage> ReadExactSkillAsync(
        string accessToken,
        string guid,
        string literalVersion,
        CancellationToken ct)
    {
        var detailRead = await client.GetExactSkillDetailAsync(accessToken, guid, literalVersion, ct);
        var packageRead = await client.GetExactSkillJsonAsync(accessToken, guid, literalVersion, ct);
        if (detailRead.ProxyStatus is not null || packageRead.ProxyStatus is not null)
            throw new InvalidOperationException($"Exact skill read-back failed for {guid}@{literalVersion}.");
        var detail = detailRead.Value
            ?? throw new InvalidOperationException($"Exact skill detail is unavailable for {guid}@{literalVersion}.");
        var package = packageRead.Value
            ?? throw new InvalidOperationException($"Exact skill package is unavailable for {guid}@{literalVersion}.");
        var exactGuid = detail.Guid;
        if (string.IsNullOrWhiteSpace(exactGuid) ||
            !string.Equals(exactGuid, guid, StringComparison.Ordinal) ||
            !string.Equals(detail.Name, package.Name, StringComparison.Ordinal) ||
            !string.Equals(package.Version, literalVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Exact skill read-back identity mismatch for {guid}@{literalVersion}.");
        }
        var files = package.Files ?? new Dictionary<string, string>();
        var bytes = files.Sum(static pair => Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value));
        return new VerifiedOrnnSkillPackage(
            exactGuid,
            package.Name ?? string.Empty,
            package.Version ?? string.Empty,
            detail.CreatedBy ?? string.Empty,
            bytes,
            files.Keys.Order(StringComparer.Ordinal).ToArray(),
            package.Metadata?.Tools?
                .Select(static tool => tool.Tool ?? string.Empty)
                .Where(static tool => tool.Length > 0)
                .Order(StringComparer.Ordinal)
                .ToArray() ?? []);
    }

    public async Task<PublishedOrnnSkillset> CreateSkillsetAsync(
        string accessToken,
        AgentProfileRolloutSkillsetPublishRequest request,
        CancellationToken ct)
    {
        var response = await client.CreateSkillSetAsync(
            accessToken,
            new OrnnSkillSetPublishRequest(
                request.Name,
                request.Description,
                request.Instructions,
                "generic",
                ["aevatar", "reviewed-profile"],
                request.Members,
                request.LiteralVersion),
            ct);
        if (!response.Succeeded || string.IsNullOrWhiteSpace(response.Skillset?.Guid))
            throw new InvalidOperationException(response.Error ?? "Ornn skillset publish returned no GUID.");
        return new PublishedOrnnSkillset(response.Skillset.Guid);
    }

    public async Task<VerifiedOrnnSkillset> ReadExactSkillsetAsync(
        string accessToken,
        string guid,
        string literalVersion,
        CancellationToken ct)
    {
        var detail = await client.GetExactSkillSetAsync(accessToken, guid, literalVersion, ct)
            ?? throw new InvalidOperationException($"Exact skillset is unavailable for {guid}@{literalVersion}.");
        var closure = await client.GetExactSkillSetClosureAsync(accessToken, guid, literalVersion, ct)
            ?? throw new InvalidOperationException($"Exact skillset closure is unavailable for {guid}@{literalVersion}.");
        var exactGuid = detail.Guid;
        if (string.IsNullOrWhiteSpace(exactGuid) || !string.Equals(exactGuid, guid, StringComparison.Ordinal))
            throw new InvalidOperationException($"Exact skillset read-back identity mismatch for {guid}@{literalVersion}.");
        return new VerifiedOrnnSkillset(
            exactGuid,
            detail.Name ?? string.Empty,
            detail.Version ?? string.Empty,
            detail.CreatedBy ?? string.Empty,
            closure.Items.Select(static item => new VerifiedOrnnSkillsetMember(
                item.Guid ?? string.Empty,
                item.Version ?? string.Empty)).ToArray());
    }
}

public enum AgentProfileRolloutCommand { None, Provision, Evaluate }

public sealed record AgentProfileRolloutCliOptions(
    AgentProfileRolloutCommand Command,
    string? InputPath,
    string? OutputDirectory,
    string? AccessTokenEnvironmentVariable,
    string? NyxIdBaseUrl,
    string? OrnnServiceSlug,
    string? Error)
{
    public bool IsValid => Command != AgentProfileRolloutCommand.None && Error is null;

    public static AgentProfileRolloutCliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is not ("provision" or "evaluate"))
            return Invalid("Usage: provision --input <release.textproto> --output <dir> --access-token-env <name> [--nyxid-base-url <url>] [--ornn-service-slug <slug>] | evaluate --input <report.pb.json>");
        var allowedOptions = args[0] == "evaluate"
            ? new HashSet<string>(["--input"], StringComparer.Ordinal)
            : new HashSet<string>(
                ["--input", "--output", "--access-token-env", "--nyxid-base-url", "--ornn-service-slug"],
                StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count ||
                !allowedOptions.Contains(args[index]) ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                return Invalid("CLI options must be --name value pairs.");
            }
        }
        if (!values.TryGetValue("--input", out var input) || string.IsNullOrWhiteSpace(input))
            return Invalid("--input is required.");
        if (args[0] == "evaluate")
            return new(AgentProfileRolloutCommand.Evaluate, input, null, null, null, null, null);
        if (!values.TryGetValue("--output", out var output) || string.IsNullOrWhiteSpace(output) ||
            !values.TryGetValue("--access-token-env", out var tokenEnv) || string.IsNullOrWhiteSpace(tokenEnv))
        {
            return Invalid("provision requires --output and --access-token-env.");
        }
        return new(
            AgentProfileRolloutCommand.Provision,
            input,
            output,
            tokenEnv,
            values.GetValueOrDefault("--nyxid-base-url"),
            values.GetValueOrDefault("--ornn-service-slug") ?? "ornn-api",
            null);
    }

    private static AgentProfileRolloutCliOptions Invalid(string error) =>
        new(AgentProfileRolloutCommand.None, null, null, null, null, null, error);
}

internal static class ReviewedReleaseTextProto
{
    public static async Task<ReviewedAgentProfileRelease> LoadAsync(string path, CancellationToken ct)
    {
        var protoPath = Path.Combine(AppContext.BaseDirectory, "protos", "agent_profile_rollout_tool.proto");
        if (!File.Exists(protoPath))
            throw new FileNotFoundException("Deployment proto was not copied next to the rollout tool.", protoPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveProtocPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($"--proto_path={Path.GetDirectoryName(protoPath)}");
        startInfo.ArgumentList.Add("--encode=aevatar.tools.agent_profile_rollout.ReviewedAgentProfileRelease");
        startInfo.ArgumentList.Add(Path.GetFileName(protoPath));
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start protoc.");
        await process.StandardInput.WriteAsync(await File.ReadAllTextAsync(path, ct));
        process.StandardInput.Close();
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Invalid reviewed release textproto: {error.Trim()}");
        return ReviewedAgentProfileRelease.Parser.ParseFrom(output.ToArray());
    }

    private static string ResolveProtocPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("PROTOC");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        var executableName = OperatingSystem.IsWindows() ? "protoc.exe" : "protoc";
        var pathCompiler = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, executableName))
            .FirstOrDefault(File.Exists);
        if (pathCompiler is not null)
            return pathCompiler;

        var bundledPath = Path.Combine(AppContext.BaseDirectory, executableName);
        return File.Exists(bundledPath) ? bundledPath : executableName;
    }
}
