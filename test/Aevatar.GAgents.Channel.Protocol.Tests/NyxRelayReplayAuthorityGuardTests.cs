using System.Diagnostics;

using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class NyxRelayReplayAuthorityGuardTests
{
    [Fact]
    public async Task NyxRelayReplayAuthorityGuard_ShouldRejectForbiddenGuardTypeAndCallbackClaimDictionary()
    {
        var repoRoot = FindRepoRoot();
        var fixtureRoot = Path.Combine(
            repoRoot,
            "agents",
            "Aevatar.GAgents.NyxidChat",
            "obj",
            "nyx-relay-guard-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var fixturePath = Path.Combine(fixtureRoot, "ForbiddenRelayReplay.cs");

        try
        {
            await File.WriteAllTextAsync(
                fixturePath,
                """
                using System.Collections.Concurrent;

                namespace GuardFixture;

                public sealed class ForbiddenRelayReplay
                {
                    private readonly ConcurrentDictionary<string, string> _callbackClaims = new();

                    public INyxIdRelayReplayGuard? ReplayGuard { get; init; }
                }
                """);

            var (exitCode, output) = await RunGuardAsync(repoRoot, fixturePath);

            exitCode.ShouldBe(1);
            output.ShouldContain("INyxIdRelayReplayGuard");
            output.ShouldContain("ConcurrentDictionary");
            output.ShouldContain("Nyx relay replay/idempotency authority must be actor-owned typed state");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunGuardAsync(string repoRoot, string fixturePath)
    {
        var scriptPath = Path.Combine(repoRoot, "tools", "ci", "guards", "nyx_relay_replay_authority_guard.py");
        var startInfo = new ProcessStartInfo("python3", $"{scriptPath} {fixturePath}")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = $"{await stdoutTask}{await stderrTask}";
        return (process.ExitCode, output);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tools", "ci", "guards", "nyx_relay_replay_authority_guard.py")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root for Nyx relay replay authority guard test.");
    }
}
