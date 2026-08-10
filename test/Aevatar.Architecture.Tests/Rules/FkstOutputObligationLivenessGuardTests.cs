using System.Diagnostics;

namespace Aevatar.Architecture.Tests.Rules;

public class FkstOutputObligationLivenessGuardTests
{
    [Fact]
    public async Task StateOutputObligationTimeoutFixtureDrainsIdempotently()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "tools", "ci", "fkst_output_obligation_liveness_guard.sh");
        var fixturePath = Path.Combine(
            repositoryRoot,
            "test",
            "Aevatar.Architecture.Tests",
            "Fixtures",
            "Fkst",
            "state-output-obligation-timeout-blocked.md");

        var result = await RunBashAsync(scriptPath, fixturePath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "state-output-obligation-timeout obligation owner path verified.",
            result.Output);
        Assert.Contains("verified_owner_package=github-devloop", result.Output);
        Assert.Contains("verified_effect_package=github-proxy", result.Output);
        Assert.Contains("verified_owner_test=restart_timeout_obligations_test", result.Output);
        Assert.Contains("verified_reconciler_test=timeout_reconcile_cas_parity_test", result.Output);
        Assert.Contains("verified_from_state=impl-failed", result.Output);
        Assert.Contains("verified_source_test=liveness_timeout_attempt_issue_test", result.Output);
        Assert.Contains(
            "verified_source_case=test_impl_failed_retry_limit_replay_decline_climbs_to_timeout_reconcile_without_seeded_timeout_markers",
            result.Output);
        Assert.Contains(
            "verified_incident_source_case=test_incident_impl_failed_timeout_source_is_redriven_from_fixture",
            result.Output);
        Assert.Contains(
            "verified_incident_reconciler_case=test_incident_impl_failed_timeout_reconcile_skips_stale_terminal_drop",
            result.Output);
        Assert.Contains(
            "verified_incident_terminal_case=test_incident_blocked_output_obligation_drains_once_from_fixture",
            result.Output);
        Assert.Contains("verified_effect_test=integration_issue_create_test", result.Output);
    }

    private static async Task<CommandResult> RunBashAsync(string scriptPath, string fixturePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "bash",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(fixturePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start bash process.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CommandResult(
            process.ExitCode,
            string.Concat(await stdout, await stderr));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed record CommandResult(int ExitCode, string Output);
}
