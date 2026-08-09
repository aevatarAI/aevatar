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
            "state-output-obligation-timeout obligation fixture drains idempotently.",
            result.Output);
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
