using Aevatar.AI.Abstractions.CodexExecution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Infrastructure.OpenSandbox.Tests;

public sealed class OpenSandboxCodexExecutionAdapterTests
{
    private const string OriginalAccessToken = "original-access-token-must-not-be-forwarded";
    private const string DelegatedAccessToken = "short-lived-llm-proxy-token";

    [Fact]
    public async Task ExecuteAsync_Success_UsesFixedLifecycleAndCleansUpBeforeCompletion()
    {
        var session = new RecordingSession();
        var credentialProvider = new RecordingCredentialProvider();
        using var adapter = CreateAdapter(session, credentialProvider);

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));

        events.Select(item => item.Kind).Should().Equal(
            CodexExecutionEventKind.Started,
            CodexExecutionEventKind.Completed);
        events[1].Result.Should().NotBeNull();
        var completed = events[1].Result!;
        completed.Output.Should().Be("CODEX_EXEC_READY");
        completed.ExitCode.Should().Be(0);
        completed.DiagnosticId.Should().StartWith("codex-");
        credentialProvider.Authority.Should().BeEquivalentTo(
            new CodexExecutionNyxIdAuthority("nyxid", "tenant-alpha", "user-alpha"));
        credentialProvider.TimeoutSeconds.Should().Be(180);
        session.BoundCredential.Should().Be(DelegatedAccessToken);
        session.BoundCredential.Should().NotBe(OriginalAccessToken);
        session.Files.Should().Contain(file =>
            file.Path == "/workspace/.aevatar/prompt.txt" &&
            file.Contents == "Reply with exactly CODEX_EXEC_READY\n");
        session.Files.Should().OnlyContain(file =>
            !file.Contents.Contains(OriginalAccessToken, StringComparison.Ordinal) &&
            !file.Contents.Contains(DelegatedAccessToken, StringComparison.Ordinal));
        session.Commands.Select(command => command.Command).Should().Equal(
            OpenSandboxCodexExecutionAdapter.GitInitializationCommand,
            OpenSandboxCodexExecutionAdapter.SandboxPreflightCommand,
            OpenSandboxCodexExecutionAdapter.FixedCodexCommand);
        session.Commands[2].Command.Should().NotContain("CODEX_EXEC_READY");
        session.Commands[2].Environment!["NYXID_LLM_TOKEN"]
            .Should().Be(SdkOpenSandboxCodexClient.FakeRunnerToken);
        session.KillCount.Should().Be(1);
        session.VerifyAbsentCount.Should().Be(1);
        session.DisposeCount.Should().Be(1);
        session.CleanupFinishedBeforeDispose.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenAccountIsNotAllowed_RejectsBeforeCredentialOrProvisioning()
    {
        var session = new RecordingSession();
        var credentialProvider = new RecordingCredentialProvider();
        var client = new RecordingClient(session);
        var options = OpenSandboxCodexOptionsValidatorTests.ValidOptions();
        options.AllowedNyxIdUserIds = ["different-user"];
        using var adapter = CreateAdapter(options, client, credentialProvider);

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));

        events[^1].Failure!.Code.Should().Be("managed_feature_not_enabled");
        credentialProvider.CallCount.Should().Be(0);
        client.CreateCount.Should().Be(0);
        session.KillCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSetupFailsAfterCreate_KillsExactlyOnce()
    {
        var session = new RecordingSession
        {
            BindException = new InvalidOperationException("vault setup failed"),
        };
        using var adapter = CreateAdapter(session, new RecordingCredentialProvider());

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));

        events[^1].Failure!.Code.Should().Be("credential_vault_binding_failed");
        session.KillCount.Should().Be(1);
        session.VerifyAbsentCount.Should().Be(1);
        session.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProvisioningFails_DoesNotInventSandboxCleanup()
    {
        var session = new RecordingSession();
        var client = new RecordingClient(session)
        {
            CreateException = new InvalidOperationException("create failed"),
        };
        using var adapter = CreateAdapter(
            OpenSandboxCodexOptionsValidatorTests.ValidOptions(),
            client,
            new RecordingCredentialProvider());

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));

        events[^1].Failure!.Code.Should().Be("sandbox_provisioning_failed");
        client.CreateCount.Should().Be(1);
        session.KillCount.Should().Be(0);
        session.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkspacePreparationFails_CleansUp()
    {
        var session = new RecordingSession
        {
            PrepareException = new InvalidOperationException("write failed"),
        };
        using var adapter = CreateAdapter(session, new RecordingCredentialProvider());

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));

        events[^1].Failure!.Code.Should().Be("workspace_preparation_failed");
        session.KillCount.Should().Be(1);
        session.VerifyAbsentCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLandlockPreflightFails_FailsClosedAndCleansUp()
    {
        var session = new RecordingSession { PreflightExitCode = 91 };
        using var adapter = CreateAdapter(session, new RecordingCredentialProvider());

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));

        var failure = events[^1].Failure!;
        failure.Kind.Should().Be(CodexExecutionFailureKind.IsolationUnavailable);
        failure.Code.Should().Be("landlock_preflight_failed");
        session.Commands.Should().HaveCount(2);
        session.KillCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCodexExitsNonzero_ReportsTerminalFailureAndCleansUp()
    {
        var session = new RecordingSession { CodexExitCode = 17 };
        using var adapter = CreateAdapter(session, new RecordingCredentialProvider());

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));

        var failure = events[^1].Failure!;
        failure.Kind.Should().Be(CodexExecutionFailureKind.TerminalFailure);
        failure.Code.Should().Be("codex_nonzero_exit");
        session.KillCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSdkCommandTimesOut_ReportsTimeoutAndCleansUp()
    {
        var session = new RecordingSession
        {
            CodexException = new OperationCanceledException("sdk timeout"),
        };
        using var adapter = CreateAdapter(session, new RecordingCredentialProvider());

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));

        var failure = events[^1].Failure!;
        failure.Kind.Should().Be(CodexExecutionFailureKind.TimedOut);
        failure.Code.Should().Be("managed_execution_timed_out");
        session.KillCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCancelsAfterCreate_ReportsCancellationAndCleansUp()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new RecordingSession
        {
            OnCodex = cancellation.Cancel,
            CodexException = new OperationCanceledException("caller cancelled"),
        };
        using var adapter = CreateAdapter(session, new RecordingCredentialProvider());

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest(), cancellation.Token));

        var failure = events[^1].Failure!;
        failure.Kind.Should().Be(CodexExecutionFailureKind.Cancelled);
        failure.Code.Should().Be("managed_execution_cancelled");
        session.KillCount.Should().Be(1);
        session.VerifyAbsentCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenJsonlIsMalformed_FailsAndCleansUp()
    {
        var session = new RecordingSession { CodexStdout = "not-json\n" };
        using var adapter = CreateAdapter(session, new RecordingCredentialProvider());

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));

        events[^1].Failure.Should().NotBeNull();
        var failure = events[^1].Failure!;
        failure.Kind.Should().Be(CodexExecutionFailureKind.MalformedOutput);
        failure.Code.Should().Be("codex_jsonl_malformed");
        session.KillCount.Should().Be(1);
        session.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCleanupCannotBeVerified_OverridesSuccessfulRun()
    {
        var session = new RecordingSession
        {
            VerifyAbsentException = new InvalidOperationException("still present"),
        };
        using var adapter = CreateAdapter(session, new RecordingCredentialProvider());

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));

        events[^1].Failure.Should().NotBeNull();
        var failure = events[^1].Failure!;
        failure.Kind.Should().Be(CodexExecutionFailureKind.CleanupFailed);
        failure.Code.Should().Be("sandbox_cleanup_failed");
        session.KillCount.Should().Be(1);
        session.VerifyAbsentCount.Should().Be(1);
        session.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_FailureSurfacesNeverContainInfrastructureOrCallerSecrets()
    {
        var session = new RecordingSession
        {
            CodexException = new InvalidOperationException(
                $"failed {OriginalAccessToken} {DelegatedAccessToken} open-sandbox-secret"),
        };
        using var adapter = CreateAdapter(session, new RecordingCredentialProvider());

        var events = await CollectAsync(adapter.ExecuteAsync(CreateRequest()));
        var serialized = System.Text.Json.JsonSerializer.Serialize(events);

        serialized.Should().NotContain(OriginalAccessToken);
        serialized.Should().NotContain(DelegatedAccessToken);
        serialized.Should().NotContain("open-sandbox-secret");
    }

    private static OpenSandboxCodexExecutionAdapter CreateAdapter(
        RecordingSession session,
        RecordingCredentialProvider credentialProvider) =>
        CreateAdapter(
            OpenSandboxCodexOptionsValidatorTests.ValidOptions(),
            new RecordingClient(session),
            credentialProvider);

    private static OpenSandboxCodexExecutionAdapter CreateAdapter(
        OpenSandboxCodexOptions options,
        RecordingClient client,
        RecordingCredentialProvider credentialProvider) =>
        new(
            Options.Create(options),
            credentialProvider,
            client,
            NullLogger<OpenSandboxCodexExecutionAdapter>.Instance);

    private static CodexExecutionRequest CreateRequest() => new(
        new CodexExecutionTarget { ManagedSandbox = new CodexManagedSandboxTarget() },
        new CodexExecutionWorkspace { EmptyGit = new CodexEmptyGitWorkspace() },
        "Reply with exactly CODEX_EXEC_READY",
        180,
        new CodexExecutionCallerContext(
            OriginalAccessToken,
            new CodexExecutionNyxIdAuthority("nyxid", "tenant-alpha", "user-alpha"),
            "scope-alpha",
            "run-alpha",
            "step-alpha",
            "call-alpha"));

    private static async Task<List<CodexExecutionEvent>> CollectAsync(
        IAsyncEnumerable<CodexExecutionEvent> source)
    {
        var events = new List<CodexExecutionEvent>();
        await foreach (var item in source)
            events.Add(item);
        return events;
    }

    private sealed class RecordingCredentialProvider : IManagedCodexCredentialProvider
    {
        public int CallCount { get; private set; }
        public CodexExecutionNyxIdAuthority? Authority { get; private set; }
        public int TimeoutSeconds { get; private set; }

        public Task<ManagedCodexCredential> IssueAsync(
            CodexExecutionNyxIdAuthority authority,
            int executionTimeoutSeconds,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            Authority = authority;
            TimeoutSeconds = executionTimeoutSeconds;
            return Task.FromResult(new ManagedCodexCredential(
                DelegatedAccessToken,
                DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()));
        }
    }

    private sealed class RecordingClient(RecordingSession session) : IOpenSandboxCodexClient
    {
        public Exception? CreateException { get; init; }
        public int CreateCount { get; private set; }

        public Task<IOpenSandboxCodexSession> CreateAsync(
            OpenSandboxCodexCreateRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateCount++;
            if (CreateException != null)
                throw CreateException;
            request.RunnerImage.Should().Be(OpenSandboxCodexOptions.PublishedRunnerImage);
            request.UseServerProxy.Should().BeTrue();
            request.ToString().Should().NotContain(OriginalAccessToken).And.NotContain(DelegatedAccessToken);
            return Task.FromResult<IOpenSandboxCodexSession>(session);
        }
    }

    private sealed class RecordingSession : IOpenSandboxCodexSession
    {
        public Exception? BindException { get; init; }
        public Exception? PrepareException { get; init; }
        public Exception? CodexException { get; init; }
        public Exception? VerifyAbsentException { get; init; }
        public Action? OnCodex { get; init; }
        public int PreflightExitCode { get; init; }
        public int CodexExitCode { get; init; }
        public string CodexStdout { get; init; } =
            "{\"type\":\"thread.started\",\"thread_id\":\"thread-1\"}\n" +
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"CODEX_EXEC_READY\"}}\n" +
            "{\"type\":\"turn.completed\"}\n";

        public string? BoundCredential { get; private set; }
        public List<OpenSandboxCodexFile> Files { get; } = [];
        public List<RecordedCommand> Commands { get; } = [];
        public int KillCount { get; private set; }
        public int VerifyAbsentCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool CleanupFinishedBeforeDispose { get; private set; }

        public Task BindCredentialAsync(
            string accessToken,
            Uri gatewayUri,
            string gatewayPathPattern,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (BindException != null)
                throw BindException;
            BoundCredential = accessToken;
            gatewayPathPattern.Should().EndWith("/*");
            return Task.CompletedTask;
        }

        public Task PrepareWorkspaceAsync(
            IReadOnlyList<OpenSandboxCodexFile> files,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (PrepareException != null)
                throw PrepareException;
            Files.AddRange(files);
            return Task.CompletedTask;
        }

        public Task<OpenSandboxCodexCommandResult> RunCommandAsync(
            string command,
            int timeoutSeconds,
            IReadOnlyDictionary<string, string>? environment,
            int maxOutputBytes,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(new RecordedCommand(command, environment));
            if (command == OpenSandboxCodexExecutionAdapter.FixedCodexCommand)
            {
                OnCodex?.Invoke();
                if (CodexException != null)
                    throw CodexException;
                return Task.FromResult(new OpenSandboxCodexCommandResult(
                    CodexExitCode,
                    CodexStdout,
                    0,
                    "execution-alpha"));
            }

            if (command == OpenSandboxCodexExecutionAdapter.SandboxPreflightCommand)
            {
                return Task.FromResult(new OpenSandboxCodexCommandResult(
                    PreflightExitCode,
                    string.Empty,
                    0,
                    null));
            }

            return Task.FromResult(new OpenSandboxCodexCommandResult(0, string.Empty, 0, null));
        }

        public Task KillAsync(CancellationToken ct = default)
        {
            KillCount++;
            return Task.CompletedTask;
        }

        public Task VerifyAbsentAsync(CancellationToken ct = default)
        {
            VerifyAbsentCount++;
            if (VerifyAbsentException != null)
                throw VerifyAbsentException;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            CleanupFinishedBeforeDispose = KillCount == 1 && VerifyAbsentCount == 1;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record RecordedCommand(
        string Command,
        IReadOnlyDictionary<string, string>? Environment);
}
