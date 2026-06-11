using System.Diagnostics;

namespace Aevatar.Architecture.Tests.Rules;

public sealed class StudioFactOwnerGuardTests
{
    [Fact]
    public async Task StudioFactOwnerGuardRejectsForbiddenProductionSymbol()
    {
        using var repo = StudioFactOwnerGuardFixture.Create();
        repo.WriteFile(
            "src/Aevatar.Studio.Application/Studio/LegacyExecutionHistory.cs",
            """
            namespace Aevatar.Studio.Application.Studio;

            public sealed class LegacyExecutionHistory
            {
                private readonly IStudioWorkspaceStore _store;
            }
            """);

        var result = await repo.RunGuardAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("LegacyExecutionHistory.cs", result.Output);
        Assert.Contains("IStudioWorkspaceStore", result.Output);
        Assert.Contains("Studio execution/workspace fact owner regression found.", result.Output);
    }

    [Fact]
    public async Task StudioFactOwnerGuardRejectsForbiddenProductionJsonFactFile()
    {
        using var repo = StudioFactOwnerGuardFixture.Create();
        repo.WriteFile("tools/studio/executions/run-001.json", "{}");

        var result = await repo.RunGuardAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("tools/studio/executions/run-001.json", NormalizePath(result.Output));
        Assert.Contains("Studio production JSON fact files are forbidden.", result.Output);
    }

    [Fact]
    public async Task StudioFactOwnerGuardRejectsServerAuthoritativeLayoutMapping()
    {
        using var repo = StudioFactOwnerGuardFixture.Create();
        repo.WriteFile(
            "src/Aevatar.Studio.Application/Studio/Services/LeakyWorkspaceService.cs",
            """
            namespace Aevatar.Studio.Application.Studio.Services;

            public sealed class LeakyWorkspaceService
            {
                public object Save(SaveWorkflowDraftRequest request) => new
                {
                    Layout = request.Layout,
                    HasLayout = true,
                };
            }
            """);

        var result = await repo.RunGuardAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("LeakyWorkspaceService.cs", result.Output);
        Assert.Contains("request.Layout", result.Output);
        Assert.Contains("Studio UI/layout facts are client-owned compatibility fields.", result.Output);
    }

    private sealed class StudioFactOwnerGuardFixture : IDisposable
    {
        private StudioFactOwnerGuardFixture(string root)
        {
            Root = root;
            Directory.CreateDirectory(Path.Combine(Root, "tools", "ci"));
            File.Copy(
                Path.Combine(FindRepositoryRoot(), "tools", "ci", "studio_fact_owner_guard.sh"),
                Path.Combine(Root, "tools", "ci", "studio_fact_owner_guard.sh"));
        }

        public string Root { get; }

        public static StudioFactOwnerGuardFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"aevatar-studio-fact-owner-guard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new StudioFactOwnerGuardFixture(root);
        }

        public void WriteFile(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public async Task<ScriptResult> RunGuardAsync()
        {
            var startInfo = new ProcessStartInfo("bash", "tools/ci/studio_fact_owner_guard.sh")
            {
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);

            return new ScriptResult(process.ExitCode, await stdout + await stderr);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "tools", "ci", "studio_fact_owner_guard.sh")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate repository root.");
        }
    }

    private static string NormalizePath(string value) => value.Replace(Path.DirectorySeparatorChar, '/');

    private sealed record ScriptResult(int ExitCode, string Output);
}
