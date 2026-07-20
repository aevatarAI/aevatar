using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Infrastructure.OpenSandbox;

public sealed class OpenSandboxCodexOptions
{
    public const string SectionName = "Aevatar:CodexExecution:ManagedSandbox";
    public const string PublishedRunnerImage =
        "ghcr.io/aevatarai/codex-runner@sha256:bad7537bc07e8e151bcbaf7fea5e334db8569d71adb563998be6f5f762e42417";

    public bool Enabled { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Protocol { get; set; } = "https";
    public bool UseServerProxy { get; set; } = true;
    public string RunnerImage { get; set; } = PublishedRunnerImage;
    public string RunnerArchitecture { get; set; } = "amd64";
    public string NyxIdGatewayUrl { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-5.4";
    public string[] AllowedNyxIdUserIds { get; set; } = [];
    public int ReadyTimeoutSeconds { get; set; } = 60;
    public int CleanupTimeoutSeconds { get; set; } = 30;
    public int MaxOutputBytes { get; set; } = 1_048_576;
    public int MaxConcurrentExecutions { get; set; } = 1;
}

internal sealed partial class OpenSandboxCodexOptionsValidator : IValidateOptions<OpenSandboxCodexOptions>
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,99}$")]
    private static partial Regex ModelPattern();

    [GeneratedRegex("^.+@sha256:[0-9a-f]{64}$")]
    private static partial Regex ImageDigestPattern();

    public ValidateOptionsResult Validate(string? name, OpenSandboxCodexOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Domain) ||
            options.Domain.Contains("://", StringComparison.Ordinal))
        {
            failures.Add("Domain must be host[:port] without a URI scheme.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            failures.Add("ApiKey must be supplied from Secret-backed configuration.");
        if (options.Protocol is not ("http" or "https"))
            failures.Add("Protocol must be 'http' or 'https'.");
        if (!options.UseServerProxy)
            failures.Add("UseServerProxy must remain true for the deployed private PSC topology.");
        if (!ImageDigestPattern().IsMatch(options.RunnerImage ?? string.Empty))
            failures.Add("RunnerImage must be immutable and digest-pinned.");
        if (options.RunnerArchitecture is not ("amd64" or "arm64"))
            failures.Add("RunnerArchitecture must be 'amd64' or 'arm64'.");
        if (!TryValidateGateway(options.NyxIdGatewayUrl))
            failures.Add("NyxIdGatewayUrl must be an HTTPS port-443 URL without query or fragment.");
        if (!ModelPattern().IsMatch(options.Model ?? string.Empty))
            failures.Add("Model contains unsupported characters.");
        if (options.AllowedNyxIdUserIds is not { Length: > 0 } ||
            options.AllowedNyxIdUserIds.Any(static value => string.IsNullOrWhiteSpace(value)))
        {
            failures.Add("AllowedNyxIdUserIds must contain at least one non-empty P0 identity.");
        }
        if (options.ReadyTimeoutSeconds is < 1 or > 120)
            failures.Add("ReadyTimeoutSeconds must be between 1 and 120.");
        if (options.CleanupTimeoutSeconds is < 1 or > 60)
            failures.Add("CleanupTimeoutSeconds must be between 1 and 60.");
        if (options.MaxOutputBytes is < 16_384 or > 1_048_576)
            failures.Add("MaxOutputBytes must be between 16384 and 1048576.");
        if (options.MaxConcurrentExecutions is < 1 or > 16)
            failures.Add("MaxConcurrentExecutions must be between 1 and 16.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool TryValidateGateway(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.IsDefaultPort &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }
}
