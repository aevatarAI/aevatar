using System.Diagnostics;

using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ChannelMegaInterfaceGuardTests
{
    [Fact]
    public async Task ChannelMegaInterfaceGuard_ShouldRejectPartialInterfaceThatCombinesRuntimeAndOutboundSurface()
    {
        var tempRoot = Directory.CreateTempSubdirectory("channel-guard-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot.FullName, "RuntimePart.cs"),
                """
                namespace GuardFixture;

                public partial interface ILeakyAdapter
                {
                    Task InitializeAsync();
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot.FullName, "OutboundPart.cs"),
                """
                namespace GuardFixture;

                public partial interface ILeakyAdapter
                {
                    Task SendAsync();
                }
                """);

            var (exitCode, output) = await RunGuardAsync(tempRoot.FullName);

            exitCode.ShouldBe(1);
            output.ShouldContain("Channel mega-interface regression detected:");
            output.ShouldContain("ILeakyAdapter");
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunGuardAsync(string scanRoot)
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "ci", "channel_mega_interface_guard.sh");
        var startInfo = new ProcessStartInfo("bash", scriptPath)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["CHANNEL_MEGA_INTERFACE_GUARD_ROOT"] = scanRoot;

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
            if (File.Exists(Path.Combine(directory.FullName, "tools", "ci", "channel_mega_interface_guard.sh")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root for channel mega-interface guard test.");
    }
}
