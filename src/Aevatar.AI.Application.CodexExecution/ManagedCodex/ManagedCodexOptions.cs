using Microsoft.Extensions.Options;

namespace Aevatar.AI.Application.CodexExecution;

public sealed class ManagedCodexOptions
{
    public const string SectionName = "Aevatar:CodexExecution:ManagedSandbox";
    public const string ChronoSandboxServiceSlug = "chrono-sandbox";
    public const string ChronoLlmServiceSlug = "chrono-llm-public";
    public const string ChronoExecutionPath = "/codex/execute";

    public bool Enabled { get; set; }
    public string[] ProvisioningAllowedNyxIdUserIds { get; set; } = [];
    public int CredentialLifetimeDays { get; set; } = 30;
    public int MaxResponseBytes { get; set; } = 1_048_576;
    public int MutationLeaseSeconds { get; set; } = 300;
    public int MutationCompletionSeconds { get; set; } = 240;

    internal bool IsProvisioningAllowed(string userId) =>
        ProvisioningAllowedNyxIdUserIds.Any(candidate =>
            string.Equals(candidate?.Trim(), userId, StringComparison.Ordinal));
}

public sealed class ManagedCodexOptionsValidator : IValidateOptions<ManagedCodexOptions>
{
    public ValidateOptionsResult Validate(string? name, ManagedCodexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        if (options.ProvisioningAllowedNyxIdUserIds.Length == 0 ||
            options.ProvisioningAllowedNyxIdUserIds.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add(
                "ProvisioningAllowedNyxIdUserIds must contain explicit internal users.");
        }
        if (options.CredentialLifetimeDays is < 1 or > 90)
            failures.Add("CredentialLifetimeDays must be between 1 and 90.");
        if (options.MaxResponseBytes is < 16_384 or > 1_048_576)
            failures.Add("MaxResponseBytes must be between 16384 and 1048576.");
        if (options.MutationCompletionSeconds is < 30 or > 600)
            failures.Add("MutationCompletionSeconds must be between 30 and 600.");
        if (options.MutationLeaseSeconds <= options.MutationCompletionSeconds ||
            options.MutationLeaseSeconds > 900)
        {
            failures.Add(
                "MutationLeaseSeconds must be greater than MutationCompletionSeconds and no more than 900.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
