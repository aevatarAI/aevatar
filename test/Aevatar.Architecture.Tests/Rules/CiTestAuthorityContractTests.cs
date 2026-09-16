using System.Diagnostics;

namespace Aevatar.Architecture.Tests.Rules;

// Test-add (test-coverage/cluster-019):
//   Covers refactor-introduced behavior in tools/ci/coverage_quality_guard.sh:34-55 and tools/ci/test_solution_ownership_guard.sh:21-66.
//   Cluster intent: strict coverage fail-fast with aevatar.slnx plus slow-tests as the only test authorities.
public class CiTestAuthorityContractTests
{
    [Fact]
    public async Task CoverageQualityGuardRunsOwnershipGuardBeforeDotnetTest()
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteAevatarSolution("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteSlowTestGuard();
        repo.WriteTestProject("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteTestProject("test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj");
        repo.WriteTestProject("test/Aevatar.Orphan.Tests/Aevatar.Orphan.Tests.csproj");

        var fakeDotnetMarker = Path.Combine(repo.Root, "dotnet-was-called");
        repo.WriteFakeDotnet($"touch {ShellQuote(fakeDotnetMarker)}\nexit 0\n");

        var result = await repo.RunScriptAsync("tools/ci/coverage_quality_guard.sh");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Found test projects outside the authoritative test surface.", result.Output);
        Assert.False(File.Exists(fakeDotnetMarker));
    }

    [Fact]
    public async Task CoverageQualityGuardFailsBeforeReportGenerationWhenDotnetTestFails()
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteAevatarSolution("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteSlowTestGuard();
        repo.WriteTestProject("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteTestProject("test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj");
        repo.WriteFakeDotnet(
            """
            for ((i=1; i<=$#; i++)); do
              if [[ "${!i}" == "--results-directory" ]]; then
                next=$((i + 1))
                mkdir -p "${!next}/fake"
                printf '<coverage />' > "${!next}/fake/coverage.cobertura.xml"
              fi
            done
            exit 7
            """);

        var result = await repo.RunScriptAsync("tools/ci/coverage_quality_guard.sh");

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("dotnet test exited with 7. Failing before coverage analysis.", result.Output);
        Assert.DoesNotContain("Restoring local tools...", result.Output);
    }

    [Fact]
    public async Task CoverageQualityGuardFailsWhenSuccessfulTestRunProducesNoCoverageFiles()
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteAevatarSolution("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteSlowTestGuard();
        repo.WriteTestProject("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteTestProject("test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj");
        repo.WriteFakeDotnet("exit 0\n");

        var result = await repo.RunScriptAsync("tools/ci/coverage_quality_guard.sh");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("dotnet test succeeded but produced no coverage files.", result.Output);
        Assert.DoesNotContain("Restoring local tools...", result.Output);
    }

    [Fact]
    public async Task TestSolutionOwnershipGuardPassesForAuthoritativeSlnxAndSingleSlowProject()
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteAevatarSolution(
            "test\\Aevatar.Unit.Tests\\Aevatar.Unit.Tests.csproj",
            "test/Aevatar.Other.Tests/Aevatar.Other.Tests.csproj");
        repo.WriteSlowTestGuard();
        repo.WriteTestProject("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteTestProject("test/Aevatar.Other.Tests/Aevatar.Other.Tests.csproj");
        repo.WriteTestProject("test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj");

        var result = await repo.RunScriptAsync("tools/ci/test_solution_ownership_guard.sh");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Test solution ownership guard passed.", result.Output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""
                dotnet test "test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj"
                dotnet test "test/Aevatar.SecondSlow.Tests/Aevatar.SecondSlow.Tests.csproj"
                """)]
    public async Task TestSolutionOwnershipGuardRejectsMissingOrMultipleSlowOwners(string slowGuardBody)
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteAevatarSolution("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteSlowTestGuard(slowGuardBody);
        repo.WriteTestProject("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");

        var result = await repo.RunScriptAsync("tools/ci/test_solution_ownership_guard.sh");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Expected exactly one slow-owned test project", result.Output);
    }

    [Fact]
    public async Task TestSolutionOwnershipGuardRejectsMissingSlowProjectPath()
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteAevatarSolution("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteSlowTestGuard();
        repo.WriteTestProject("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");

        var result = await repo.RunScriptAsync("tools/ci/test_solution_ownership_guard.sh");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("slow_test_guards.sh points to a missing project", result.Output);
    }

    [Fact]
    public async Task TestSolutionOwnershipGuardRejectsMissingSlnxProjectPath()
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteAevatarSolution("test/Aevatar.Missing.Tests/Aevatar.Missing.Tests.csproj");
        repo.WriteSlowTestGuard();
        repo.WriteTestProject("test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj");

        var result = await repo.RunScriptAsync("tools/ci/test_solution_ownership_guard.sh");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("aevatar.slnx test entry points to a missing project", result.Output);
    }

    [Fact]
    public async Task TestSolutionOwnershipGuardRejectsOrphanTestProject()
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteAevatarSolution("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteSlowTestGuard();
        repo.WriteTestProject("test/Aevatar.Unit.Tests/Aevatar.Unit.Tests.csproj");
        repo.WriteTestProject("test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj");
        repo.WriteTestProject("test/Aevatar.Orphan.Tests/Aevatar.Orphan.Tests.csproj");

        var result = await repo.RunScriptAsync("tools/ci/test_solution_ownership_guard.sh");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Found test projects outside the authoritative test surface.", result.Output);
        Assert.Contains("test/Aevatar.Orphan.Tests/Aevatar.Orphan.Tests.csproj", result.Output);
    }

    [Fact]
    public async Task RuntimeCallbackGuardPassesForGeneratedProtoSchedulerState()
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteFoundationRuntimeSource(
            "src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs",
            """
            private readonly IPersistentState<RuntimeCallbackSchedulerState> _state;
            public RuntimeCallbackSchedulerGrain(
                [PersistentState("runtime-callback-scheduler-v2", OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName)]
                IPersistentState<RuntimeCallbackSchedulerState> state) {}
            """);

        var result = await repo.RunScriptAsync("tools/ci/runtime_callback_guards.sh");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Runtime callback guards passed.", result.Output);
    }

    [Fact]
    public async Task RuntimeCallbackGuardRejectsHandwrittenCallbackPayloadState()
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteFoundationRuntimeSource(
            "src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrainState.cs",
            """
            public sealed class RuntimeCallbackSchedulerGrainState {}
            public sealed class ReminderScheduledCallbackState
            {
                public byte[] EnvelopeBytes { get; set; } = [];
            }
            """);

        var result = await repo.RunScriptAsync("tools/ci/runtime_callback_guards.sh");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Durable runtime callback scheduler state must use generated protobuf RuntimeCallbackSchedulerState", result.Output);
        Assert.Contains("EnvelopeBytes", result.Output);
    }

    [Fact]
    public async Task RuntimeCallbackGuardRejectsSchedulerStateOnSharedStorage()
    {
        using var repo = TemporaryCiRepo.Create();
        repo.WriteFoundationRuntimeSource(
            "src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/Callbacks/RuntimeCallbackSchedulerGrain.cs",
            """
            private readonly IPersistentState<RuntimeCallbackSchedulerState> _state;
            public RuntimeCallbackSchedulerGrain(
                [PersistentState("runtime-callback-scheduler-v2", OrleansRuntimeConstants.GrainStateStorageName)]
                IPersistentState<RuntimeCallbackSchedulerState> state) {}
            """);

        var result = await repo.RunScriptAsync("tools/ci/runtime_callback_guards.sh");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Runtime callback scheduler grain must use isolated RuntimeCallbackSchedulerStorageName", result.Output);
    }

    [Fact]
    public void MainFlowRuntimeSmoke_ShouldUseInMemorySecretStore()
    {
        var scriptPath = Path.Combine(TemporaryCiRepo.FindRepositoryRoot(), "tools", "ci", "main_flow_runtime_smoke.sh");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("AEVATAR_ActorRuntime__Provider=Orleans", script, StringComparison.Ordinal);
        Assert.Contains("AEVATAR_ActorRuntime__OrleansPersistenceBackend=InMemory", script, StringComparison.Ordinal);
        Assert.Contains("AEVATAR_ActorRuntime__SecretStoreBackend=InMemory", script, StringComparison.Ordinal);
        Assert.Contains("start_nyxid_stub", script, StringComparison.Ordinal);
        Assert.Contains("wait_for_schedule_provisioning", script, StringComparison.Ordinal);
        Assert.Contains("scheduleProvisioningId", script, StringComparison.Ordinal);
        Assert.Contains("/health/ready", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("distributed_3node_smoke.sh")]
    [InlineData("distributed_mixed_version_smoke.sh")]
    public void DistributedRuntimeSmoke_ShouldUseSyntheticSecretsAndAuthenticatedApiProbes(string scriptName)
    {
        var scriptPath = Path.Combine(TemporaryCiRepo.FindRepositoryRoot(), "tools", "ci", scriptName);
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("create_synthetic_secret_store_keyring", script, StringComparison.Ordinal);
        Assert.Contains("create_synthetic_scope_service_token", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Aevatar__Authentication__Enabled=false", script, StringComparison.Ordinal);
        Assert.Contains("Aevatar__Authentication__Authority=\"\"", script, StringComparison.Ordinal);
        Assert.Contains("Aevatar__Authentication__ScopeServiceTokens__Enabled=true", script, StringComparison.Ordinal);
        Assert.Contains("Aevatar__Authentication__ScopeServiceTokens__SigningKeys__0__KeyBase64", script, StringComparison.Ordinal);
        Assert.Contains("/health/ready", script, StringComparison.Ordinal);
        Assert.Contains("AEVATAR_TEST_CLUSTER_BEARER_TOKEN", script, StringComparison.Ordinal);
        Assert.Contains("AEVATAR_ActorRuntime__SecretStoreBackend=Garnet", script, StringComparison.Ordinal);
        Assert.Contains("AEVATAR_ActorRuntime__SecretStoreKeyringPath", script, StringComparison.Ordinal);
        Assert.Contains("Audit__ActorIdentityHasher__ActiveKeyId=distributed-smoke-key", script, StringComparison.Ordinal);
        Assert.Contains("Audit__ActorIdentityHasher__Keys__0__KeyId=distributed-smoke-key", script, StringComparison.Ordinal);
        Assert.Contains("Audit__ActorIdentityHasher__Keys__0__Key=", script, StringComparison.Ordinal);
        Assert.Contains("ChannelIdentity__OAuthClient__Bootstrap__Enabled=false", script, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet build test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj",
            script,
            StringComparison.Ordinal);
        Assert.Contains("--no-build", script, StringComparison.Ordinal);
        Assert.Contains("--no-restore", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DistributedMixedVersionSmoke_ShouldRetryOnlyTransientEventProbeFailures()
    {
        var scriptPath = Path.Combine(
            TemporaryCiRepo.FindRepositoryRoot(),
            "tools",
            "ci",
            "distributed_mixed_version_smoke.sh");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("max_attempts = 5", script, StringComparison.Ordinal);
        Assert.Contains("retryable_status_codes = {502, 503, 504}", script, StringComparison.Ordinal);
        Assert.Contains(
            "except (ConnectionError, urllib.error.URLError, TimeoutError, socket.timeout)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("error.code not in retryable_status_codes", script, StringComparison.Ordinal);
        Assert.Contains("file=sys.stderr", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DistributedMixedVersionSmoke_ShouldWaitForCommittedWorkflowDetailReadiness()
    {
        var scriptPath = Path.Combine(
            TemporaryCiRepo.FindRepositoryRoot(),
            "tools",
            "ci",
            "distributed_mixed_version_smoke.sh");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("query_workflow_name()", script, StringComparison.Ordinal);
        Assert.Contains("method=\"GET\"", script, StringComparison.Ordinal);
        Assert.Contains("required_workflow = \"mission_wall_15_node_probe\"", script, StringComparison.Ordinal);
        Assert.Contains("max_attempts = 10", script, StringComparison.Ordinal);
        Assert.Contains("/api/workflows/", script, StringComparison.Ordinal);
        Assert.Contains("urllib.parse.quote(required_workflow, safe='')", script, StringComparison.Ordinal);
        Assert.Contains("readiness_status_codes = {404, 502, 503, 504}", script, StringComparison.Ordinal);
        Assert.Contains("if workflow_name == required_workflow:", script, StringComparison.Ordinal);
        Assert.Contains(
            "Workflow detail readiness probe attempt {attempt}/{max_attempts}",
            script,
            StringComparison.Ordinal);
        Assert.Contains("error.code not in readiness_status_codes", script, StringComparison.Ordinal);
        Assert.Contains(
            "except (ConnectionError, urllib.error.URLError, TimeoutError, socket.timeout)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Workflow detail readiness probe returned invalid JSON.", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DistributedMixedVersionSmoke_ShouldBoundTypedEventProbeFailureDiagnostics()
    {
        var scriptPath = Path.Combine(
            TemporaryCiRepo.FindRepositoryRoot(),
            "tools",
            "ci",
            "distributed_mixed_version_smoke.sh");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("print_event_probe_failure_diagnostic()", script, StringComparison.Ordinal);
        Assert.Contains("payload.get(\"code\")", script, StringComparison.Ordinal);
        Assert.Contains("payload.get(\"message\")", script, StringComparison.Ordinal);
        Assert.Contains("[:160]", script, StringComparison.Ordinal);
        Assert.Contains(
            "print_event_probe_failure_diagnostic \"${probe_log_file}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("print_event_probe_server_diagnostic()", script, StringComparison.Ordinal);
        Assert.Contains("Workflow chat execution failed.", script, StringComparison.Ordinal);
        Assert.Contains("if len(diagnostics) == 5:", script, StringComparison.Ordinal);
        Assert.Contains("[:200]", script, StringComparison.Ordinal);
        Assert.Contains("Bearer <redacted>", script, StringComparison.Ordinal);
        Assert.Contains(
            "print_event_probe_server_diagnostic \"${log_dir}/node1.log\"",
            script,
            StringComparison.Ordinal);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed class TemporaryCiRepo : IDisposable
    {
        private readonly string _fakeBin;

        private TemporaryCiRepo(string root)
        {
            Root = root;
            _fakeBin = Path.Combine(root, "fake-bin");
            Directory.CreateDirectory(_fakeBin);
            Directory.CreateDirectory(Path.Combine(root, "tools", "ci"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Aevatar.Foundation.Runtime"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Aevatar.Foundation.Runtime.Implementations.Orleans"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Aevatar.Foundation.Runtime.Implementations.Orleans", "Grains"));
            Directory.CreateDirectory(Path.Combine(root, "src", "workflow", "Aevatar.Workflow.Core", "Modules"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Aevatar.Scripting.Core"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Aevatar.Scripting.Abstractions"));
            File.WriteAllText(
                Path.Combine(root, "src", "Aevatar.Foundation.Runtime.Implementations.Orleans", "Grains", "RuntimeActorGrain.cs"),
                "");
            File.WriteAllText(
                Path.Combine(root, "src", "Aevatar.Scripting.Core", "ScriptBehaviorGAgent.cs"),
                "");
            File.WriteAllText(
                Path.Combine(root, "src", "Aevatar.Scripting.Abstractions", "script_host_messages.proto"),
                "");
            File.Copy(
                Path.Combine(FindRepositoryRoot(), "tools", "ci", "coverage_quality_guard.sh"),
                Path.Combine(root, "tools", "ci", "coverage_quality_guard.sh"));
            File.Copy(
                Path.Combine(FindRepositoryRoot(), "tools", "ci", "test_solution_ownership_guard.sh"),
                Path.Combine(root, "tools", "ci", "test_solution_ownership_guard.sh"));
            File.Copy(
                Path.Combine(FindRepositoryRoot(), "tools", "ci", "runtime_callback_guards.sh"),
                Path.Combine(root, "tools", "ci", "runtime_callback_guards.sh"));
        }

        public string Root { get; }

        public static TemporaryCiRepo Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"aevatar-ci-contract-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new TemporaryCiRepo(root);
        }

        public void WriteAevatarSolution(params string[] testProjectPaths)
        {
            var projects = string.Join(
                Environment.NewLine,
                testProjectPaths.Select(path => $"  <Project Path=\"{path}\" />"));
            File.WriteAllText(
                Path.Combine(Root, "aevatar.slnx"),
                $"<Solution>{Environment.NewLine}{projects}{Environment.NewLine}</Solution>{Environment.NewLine}");
        }

        public void WriteSlowTestGuard(string? body = null)
        {
            var path = Path.Combine(Root, "tools", "ci", "slow_test_guards.sh");
            File.WriteAllText(
                path,
                body ?? "dotnet test \"test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj\"\n");
        }

        public void WriteTestProject(string relativePath)
        {
            var fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        }

        public void WriteFakeDotnet(string body)
        {
            var path = Path.Combine(_fakeBin, "dotnet");
            File.WriteAllText(path, $"#!/usr/bin/env bash{Environment.NewLine}{body}");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        public void WriteFoundationRuntimeSource(string relativePath, string body)
        {
            var fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, body);
        }

        public async Task<ScriptResult> RunScriptAsync(string relativePath)
        {
            var startInfo = new ProcessStartInfo("bash", relativePath)
            {
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.Environment["PATH"] = _fakeBin + Path.PathSeparator + startInfo.Environment["PATH"];
            startInfo.Environment["AEVATAR_CI_RG_BIN"] = "__aevatar_missing_rg__";

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

        public static string FindRepositoryRoot()
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
    }

    private sealed record ScriptResult(int ExitCode, string Output);
}
