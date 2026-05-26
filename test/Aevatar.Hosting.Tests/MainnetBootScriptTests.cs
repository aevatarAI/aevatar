using System.Diagnostics;
using FluentAssertions;

namespace Aevatar.Hosting.Tests;

public sealed class MainnetBootScriptTests
{
    [Fact]
    public async Task BootScript_LocalMode_ShouldNotRequireNeo4jPasswordFromInheritedDistributedEnv()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);

        using var process = Process.Start(CreateProcessStartInfo(scriptPath, tempDir.Path));
        process.Should().NotBeNull();

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().NotBe(0);
        stderr.Should().NotContain("Distributed mode with Neo4j enabled requires an explicit Neo4j password.");
        stderr.Should().Contain("Aevatar.Mainnet.Host.Api failed to start.");
        stdout.Should().Contain("==> Starting Aevatar.Mainnet.Host.Api");
    }

    [Fact]
    public async Task BootScript_DistributedMode_ShouldNotRequireNeo4jPassword_WhenNeo4jDisabled()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);

        using var process = Process.Start(CreateProcessStartInfo(
            scriptPath,
            tempDir.Path,
            "distributed",
            new Dictionary<string, string?>
            {
                ["AEVATAR_Projection__Graph__Providers__Neo4j__Enabled"] = "false",
            }));
        process.Should().NotBeNull();

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().NotBe(0);
        stderr.Should().NotContain("Distributed mode with Neo4j enabled requires an explicit Neo4j password.");
        stderr.Should().Contain("Aevatar.Mainnet.Host.Api failed to start.");
        stdout.Should().Contain("==> Mode: distributed");
    }

    [Fact]
    public async Task BootScript_DistributedMode_ShouldRequireNeo4jPassword_ByDefault()
    {
        var repoRoot = FindRepoRoot();
        var sourceDir = Path.Combine(repoRoot, "src", "Aevatar.Mainnet.Host.Api");

        using var tempDir = new TemporaryDirectory();
        var scriptPath = Path.Combine(tempDir.Path, "boot.sh");
        var projectPath = Path.Combine(tempDir.Path, "Aevatar.Mainnet.Host.Api.csproj");
        File.Copy(Path.Combine(sourceDir, "boot.sh"), scriptPath);
        File.Copy(Path.Combine(sourceDir, "Aevatar.Mainnet.Host.Api.csproj"), projectPath);

        using var process = Process.Start(CreateProcessStartInfo(
            scriptPath,
            tempDir.Path,
            "distributed",
            new Dictionary<string, string?>
            {
                ["AEVATAR_Projection__Graph__Providers__Neo4j__Enabled"] = null,
            }));
        process.Should().NotBeNull();

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().NotBe(0);
        stderr.Should().Contain("Distributed mode with Neo4j enabled requires an explicit Neo4j password.");
        stderr.Should().NotContain("Aevatar.Mainnet.Host.Api failed to start.");
        stdout.Should().Contain("==> Mode: distributed");
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string scriptPath,
        string workingDirectory,
        string mode = "local",
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("5187");

        startInfo.Environment["DOTNET_CMD"] = "/usr/bin/false";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Distributed";
        startInfo.Environment["AEVATAR_Projection__Graph__Providers__Neo4j__Enabled"] = "true";
        startInfo.Environment.Remove("AEVATAR_Projection__Graph__Providers__Neo4j__Password");
        startInfo.Environment.Remove("NEO4J_PASSWORD");

        if (overrides is not null)
        {
            foreach (var entry in overrides)
            {
                if (entry.Value is null)
                    startInfo.Environment.Remove(entry.Key);
                else
                    startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        return startInfo;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aevatar-mainnet-boot-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
