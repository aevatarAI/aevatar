using Microsoft.Extensions.Options;

namespace Aevatar.AI.Application.CodexExecution;

public enum ManagedCodexEligibilityMode
{
    Allowlist = 0,
    All = 1,
}

public sealed class ManagedCodexEligibilityOptions
{
    public ManagedCodexEligibilityMode Mode { get; set; } =
        ManagedCodexEligibilityMode.Allowlist;

    public string[] AllowedNyxIdUserIds { get; set; } = [];
}

public sealed class ManagedCodexOptions
{
    public const string SectionName = "Aevatar:CodexExecution:ManagedSandbox";
    public const string ChronoSandboxServiceSlug = "chrono-sandbox";
    public const string ChronoLlmServiceSlug = "chrono-llm-public";
    public const string ChronoExecutionPath = "/codex/execute";
    internal const int MutationLeaseSafetySeconds = 10;
    internal const int MutationCompensationReserveSeconds = 10;
    internal const int MutationRecordingReserveSeconds = 10;
    internal const int RequiredMutationLeaseMarginSeconds =
        MutationLeaseSafetySeconds +
        MutationCompensationReserveSeconds +
        MutationRecordingReserveSeconds;

    public bool Enabled { get; set; }
    public ManagedCodexEligibilityOptions Eligibility { get; set; } = new();
    public int CredentialLifetimeDays { get; set; } = 30;
    public int MaxResponseBytes { get; set; } = 1_048_576;
    public int MutationLeaseSeconds { get; set; } = 300;
    public int MutationCompletionSeconds { get; set; } = 240;

    public bool IsEligible(string userId)
    {
        var normalized = userId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return Eligibility.Mode == ManagedCodexEligibilityMode.All ||
               Eligibility.AllowedNyxIdUserIds.Any(candidate =>
                   string.Equals(candidate?.Trim(), normalized, StringComparison.Ordinal));
    }
}

public sealed class ManagedCodexOptionsValidator : IValidateOptions<ManagedCodexOptions>
{
    public ValidateOptionsResult Validate(string? name, ManagedCodexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        var users = options.Eligibility?.AllowedNyxIdUserIds ?? [];
        if (options.Eligibility is null)
            failures.Add("Eligibility is required.");
        else if (options.Eligibility.Mode == ManagedCodexEligibilityMode.Allowlist &&
                 (users.Length == 0 ||
                  users.Any(string.IsNullOrWhiteSpace) ||
                  users.Select(static value => value.Trim()).Distinct(StringComparer.Ordinal).Count() != users.Length))
            failures.Add("Eligibility.Allowlist requires normalized distinct AllowedNyxIdUserIds.");
        else if (options.Eligibility.Mode == ManagedCodexEligibilityMode.All && users.Length != 0)
            failures.Add("Eligibility.All requires an empty AllowedNyxIdUserIds list.");
        else if (!Enum.IsDefined(options.Eligibility.Mode))
            failures.Add("Eligibility.Mode must be Allowlist or All.");
        if (options.CredentialLifetimeDays is < 1 or > 90)
            failures.Add("CredentialLifetimeDays must be between 1 and 90.");
        if (options.MaxResponseBytes is < 16_384 or > 1_048_576)
            failures.Add("MaxResponseBytes must be between 16384 and 1048576.");
        if (options.MutationCompletionSeconds is < 30 or > 600)
            failures.Add("MutationCompletionSeconds must be between 30 and 600.");
        if (options.MutationLeaseSeconds <
            options.MutationCompletionSeconds +
            ManagedCodexOptions.RequiredMutationLeaseMarginSeconds ||
            options.MutationLeaseSeconds > 900)
        {
            failures.Add(
                $"MutationLeaseSeconds must be at least MutationCompletionSeconds + {ManagedCodexOptions.RequiredMutationLeaseMarginSeconds} and no more than 900.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
