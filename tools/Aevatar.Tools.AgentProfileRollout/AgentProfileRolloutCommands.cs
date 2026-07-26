using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.Tools.AgentProfileRollout.Contracts;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Tools.AgentProfileRollout;

public sealed class AgentProfileRolloutCommands
{
    public const string ReleaseSpecFileName = "reviewed-release.json";
    private const int FullCohortBasisPoints = 10_000;
    private const string NyxIdChatProfileSlug = "nyxid-chat";
    private static readonly JsonParser ReleaseSpecParser = new(
        JsonParser.Settings.Default.WithIgnoreUnknownFields(false));
    private static readonly JsonFormatter ProtoJsonFormatter = new(
        JsonFormatter.Settings.Default.WithIndentation("  "));
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
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
            ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
            var releaseSpec = await ReadReleaseSpecAsync(releaseSpecPath, ct);
            ValidateReleaseSpec(releaseSpec);
            var outputAlreadyProvisioned = await ValidateOutputTargetAsync(
                outputDirectory,
                releaseSpec,
                ct);

            foreach (var expected in releaseSpec.ExpectedExactSkillClosure)
            {
                var actual = await _ornnGateway.ReadExactSkillAsync(
                    accessToken,
                    expected.SkillGuid,
                    expected.LiteralVersion,
                    ct);
                VerifyExactSkill(expected, actual);
            }

            if (outputAlreadyProvisioned)
            {
                await ValidateExistingOutputAsync(outputDirectory, releaseSpec, ct);
                return 0;
            }

            await WriteNewOutputDirectoryAtomicallyAsync(outputDirectory, releaseSpec, ct);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Agent profile rollout provisioning failed closed");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static async Task<AgentProfileRolloutReleaseSpec> ReadReleaseSpecAsync(
        string path,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var json = StrictUtf8.GetString(bytes);
        return ReleaseSpecParser.Parse<AgentProfileRolloutReleaseSpec>(json);
    }

    public static byte[] FormatReleaseSpecUtf8(AgentProfileRolloutReleaseSpec releaseSpec)
    {
        ArgumentNullException.ThrowIfNull(releaseSpec);
        using var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        ProtoJsonFormatter.Format(releaseSpec, writer);
        writer.Write('\n');
        return StrictUtf8.GetBytes(writer.ToString());
    }

    public static void ValidateReleaseSpec(AgentProfileRolloutReleaseSpec releaseSpec)
    {
        ArgumentNullException.ThrowIfNull(releaseSpec);
        RequireCanonicalValue(releaseSpec.ReleaseId, "release_id");
        RequireCanonicalValue(releaseSpec.Stage, "stage");
        RequireCanonicalValue(releaseSpec.CohortSalt, "cohort_salt");

        if (releaseSpec.ProfileReference is null ||
            !string.Equals(
                releaseSpec.ProfileReference.OwnerHandle,
                AgentProfilePolicies.SystemOwnerHandle,
                StringComparison.Ordinal) ||
            !string.Equals(
                releaseSpec.ProfileReference.ProfileSlug,
                NyxIdChatProfileSlug,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "NyxID chat rollout requires the typed Profile reference 'system/nyxid-chat'.");
        }

        if (releaseSpec.ActivationMode is not (
                AgentProfileRolloutActivationMode.Shadow or
                AgentProfileRolloutActivationMode.Enforced))
        {
            throw new InvalidOperationException(
                "Agent Profile rollout activation mode must be SHADOW or ENFORCED.");
        }

        if (releaseSpec.CohortBasisPoints is <= 0 or > FullCohortBasisPoints)
        {
            throw new InvalidOperationException(
                "Agent Profile rollout cohort basis points must be in 1..10000.");
        }

        if (releaseSpec.ExpectedPublishedRevision <= 0 ||
            releaseSpec.ExpectedPublishedSnapshotSha256.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidOperationException(
                "Agent Profile rollout requires one published revision and 32-byte snapshot digest pin.");
        }

        ValidateExactClosure(releaseSpec.ExpectedExactSkillClosure);
        ValidateRuntimeBounds(releaseSpec.RuntimeBounds);
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
        Console.WriteLine(ProtoJsonFormatter.Format(decision));
        return decision.Accepted ? 0 : 1;
    }

    private static void ValidateExactClosure(
        IEnumerable<ExactOrnnSkillReference> exactClosure)
    {
        var entries = exactClosure.ToArray();
        if (entries.Length is < 1 or > 32)
        {
            throw new InvalidOperationException(
                "Agent Profile rollout exact closure must contain between 1 and 32 skills.");
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exactReference in entries)
        {
            if (AgentProfilePolicies.ValidateExactSkillReference(exactReference).Count > 0)
                throw new InvalidOperationException("Agent Profile rollout exact closure is invalid.");
            if (!identities.Add(ExactIdentity(exactReference)))
                throw new InvalidOperationException("Agent Profile rollout exact closure must be unique.");
        }

        if (!entries.Select(ExactIdentity).SequenceEqual(
                entries.Select(ExactIdentity).Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Agent Profile rollout exact closure must use canonical order.");
        }
    }

    private static void ValidateRuntimeBounds(AgentProfileRolloutRuntimeBounds? bounds)
    {
        if (bounds is null ||
            bounds.MaxPlanSteps != 4 ||
            bounds.HandoffTtlSeconds != 900 ||
            bounds.ClassifierTimeoutMs != 600 ||
            bounds.MaxSelectedSkillBytes != 24_576)
        {
            throw new InvalidOperationException(
                "NyxID chat rollout runtime bounds must be 4/900/600/24576.");
        }
    }

    private static void VerifyExactSkill(
        ExactOrnnSkillReference expected,
        VerifiedExactOrnnSkill actual)
    {
        if (!string.Equals(actual.Guid, expected.SkillGuid, StringComparison.Ordinal) ||
            !string.Equals(actual.LiteralVersion, expected.LiteralVersion, StringComparison.Ordinal) ||
            !string.Equals(actual.Name, expected.ExpectedName, StringComparison.Ordinal) ||
            !string.Equals(actual.PublisherId, expected.ExpectedPublisherId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Exact skill identity mismatch for {expected.SkillGuid}@{expected.LiteralVersion}.");
        }
    }

    private static async Task<bool> ValidateOutputTargetAsync(
        string outputDirectory,
        AgentProfileRolloutReleaseSpec releaseSpec,
        CancellationToken ct)
    {
        if (!FileSystemEntryExists(outputDirectory))
            return false;

        await ValidateExistingOutputAsync(outputDirectory, releaseSpec, ct);
        return true;
    }

    private static async Task ValidateExistingOutputAsync(
        string outputDirectory,
        AgentProfileRolloutReleaseSpec releaseSpec,
        CancellationToken ct)
    {
        if (!Directory.Exists(outputDirectory))
        {
            throw new InvalidOperationException(
                "The rollout output path must be a regular non-link directory.");
        }

        var outputDirectoryInfo = new DirectoryInfo(outputDirectory);
        if (outputDirectoryInfo.LinkTarget is not null ||
            (outputDirectoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The rollout output directory must be a regular non-link directory.");
        }

        var entries = Directory.GetFileSystemEntries(outputDirectory);
        var manifestPath = Path.Combine(outputDirectory, ReleaseSpecFileName);
        if (entries.Length != 1 ||
            !string.Equals(
                Path.GetFullPath(entries[0]),
                Path.GetFullPath(manifestPath),
                StringComparison.Ordinal) ||
            !File.Exists(manifestPath) ||
            (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"The rollout output directory must contain only '{ReleaseSpecFileName}'.");
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        var manifestJson = StrictUtf8.GetString(manifestBytes);
        var parsed = ReleaseSpecParser.Parse<AgentProfileRolloutReleaseSpec>(manifestJson);
        var canonicalBytes = FormatReleaseSpecUtf8(releaseSpec);
        if (!parsed.Equals(releaseSpec) ||
            !manifestBytes.AsSpan().SequenceEqual(canonicalBytes))
        {
            throw new InvalidOperationException(
                "The existing rollout output manifest must canonically equal the requested release spec.");
        }
    }

    private static async Task WriteNewOutputDirectoryAtomicallyAsync(
        string outputDirectory,
        AgentProfileRolloutReleaseSpec releaseSpec,
        CancellationToken ct)
    {
        var outputPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var outputName = Path.GetFileName(outputPath);
        var parentDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputName) || string.IsNullOrWhiteSpace(parentDirectory))
            throw new InvalidOperationException("The rollout output directory must have a parent directory.");

        Directory.CreateDirectory(parentDirectory);
        if (FileSystemEntryExists(outputPath))
            throw new InvalidOperationException("The rollout output path appeared during exact skill verification.");

        var stagingDirectory = Path.Combine(
            parentDirectory,
            $".{outputName}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var stagingManifestPath = Path.Combine(stagingDirectory, ReleaseSpecFileName);
            await File.WriteAllBytesAsync(
                stagingManifestPath,
                FormatReleaseSpecUtf8(releaseSpec),
                ct);
            await ValidateExistingOutputAsync(stagingDirectory, releaseSpec, ct);
            Directory.Move(stagingDirectory, outputPath);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static bool FileSystemEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void RequireCanonicalValue(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"Agent Profile rollout field '{fieldName}' must be canonical.");
        }
    }

    private static string ExactIdentity(ExactOrnnSkillReference reference) =>
        $"{reference.SkillGuid}\0{reference.LiteralVersion}\0{reference.ExpectedName}\0{reference.ExpectedPublisherId}";

    private static void AddZeroInvariant(List<string> violations, int value, string name)
    {
        if (value != 0)
            violations.Add($"{name}_must_be_zero");
    }

    private static double Percentage(int numerator, int denominator) =>
        denominator == 0 ? 100 : numerator * 100d / denominator;

    private static bool IsNonnegativeFinite(double value) =>
        double.IsFinite(value) && value >= 0;
}

public interface IAgentProfileRolloutOrnnGateway
{
    Task<VerifiedExactOrnnSkill> ReadExactSkillAsync(
        string accessToken,
        string guid,
        string literalVersion,
        CancellationToken ct);
}

public sealed record VerifiedExactOrnnSkill(
    string Guid,
    string Name,
    string LiteralVersion,
    string PublisherId);

public sealed class OrnnAgentProfileRolloutGateway(OrnnSkillClient client)
    : IAgentProfileRolloutOrnnGateway
{
    public async Task<VerifiedExactOrnnSkill> ReadExactSkillAsync(
        string accessToken,
        string guid,
        string literalVersion,
        CancellationToken ct)
    {
        var detailRead = await client.GetExactSkillDetailAsync(accessToken, guid, literalVersion, ct);
        if (detailRead.ProxyStatus is not null)
            throw new InvalidOperationException($"Exact skill read-back failed for {guid}@{literalVersion}.");
        var detail = detailRead.Value
            ?? throw new InvalidOperationException($"Exact skill detail is unavailable for {guid}@{literalVersion}.");

        var packageRead = await client.GetExactSkillJsonAsync(accessToken, guid, literalVersion, ct);
        if (packageRead.ProxyStatus is not null)
            throw new InvalidOperationException($"Exact skill read-back failed for {guid}@{literalVersion}.");
        var package = packageRead.Value
            ?? throw new InvalidOperationException($"Exact skill package is unavailable for {guid}@{literalVersion}.");
        if (string.IsNullOrWhiteSpace(detail.Guid) ||
            !string.Equals(detail.Guid, guid, StringComparison.Ordinal) ||
            !string.Equals(detail.Name, package.Name, StringComparison.Ordinal) ||
            !string.Equals(package.Version, literalVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Exact skill read-back identity mismatch for {guid}@{literalVersion}.");
        }

        return new VerifiedExactOrnnSkill(
            detail.Guid,
            package.Name ?? string.Empty,
            package.Version ?? string.Empty,
            detail.CreatedBy ?? string.Empty);
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
        {
            return Invalid(
                "Usage: provision --input <reviewed-release.json> --output <dir> --access-token-env <name> [--nyxid-base-url <url>] [--ornn-service-slug <slug>] | evaluate --input <report.pb.json>");
        }

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
