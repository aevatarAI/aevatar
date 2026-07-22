using Aevatar.AI.Abstractions.CodexExecution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class ChronoSandboxCodexExecutionAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_Success_EmitsStartedThenCompleted()
    {
        var client = new RecordingClient
        {
            Result = new CodexExecutionResult("CODEX_EXEC_READY", 0, "chrono-1", 125),
        };
        var adapter = CreateAdapter(client);

        var events = await CollectAsync(adapter.ExecuteAsync(Request()));

        events.Select(static item => item.Kind).Should().Equal(
            CodexExecutionEventKind.Started,
            CodexExecutionEventKind.Completed);
        events[1].Result.Should().BeEquivalentTo(client.Result);
        client.Request.Should().BeEquivalentTo(Request());
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_RejectsBeforeCredentialResolution()
    {
        var client = new RecordingClient();
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.Enabled = false;
        var adapter = CreateAdapter(client, options);

        var events = await CollectAsync(adapter.ExecuteAsync(Request()));

        events[^1].Failure!.Kind.Should().Be(CodexExecutionFailureKind.TargetNotConfigured);
        events[^1].Failure!.Code.Should().Be("managed_target_disabled");
        client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenClientFails_ReturnsOnlyTheSanitizedTypedFailure()
    {
        var client = new RecordingClient
        {
            Exception = new ManagedCodexExecutionException(
                new CodexExecutionFailure(
                    CodexExecutionFailureKind.AdmissionDenied,
                    "managed_credential_unavailable",
                    "Managed Codex credential is unavailable.")),
        };
        var adapter = CreateAdapter(client);

        var events = await CollectAsync(adapter.ExecuteAsync(Request()));

        events[^1].Failure!.Code.Should().Be("managed_credential_unavailable");
        System.Text.Json.JsonSerializer.Serialize(events).Should()
            .NotContain("raw-agent-key");
    }

    [Fact]
    public async Task ExecuteAsync_WhenProxyTimesOut_ReturnsTimedOutFailure()
    {
        var client = new RecordingClient { Exception = new TaskCanceledException("HTTP timeout") };
        var adapter = CreateAdapter(client);

        var events = await CollectAsync(adapter.ExecuteAsync(Request()));

        events[^1].Failure!.Kind.Should().Be(CodexExecutionFailureKind.TimedOut);
        events[^1].Failure!.Code.Should().Be("managed_proxy_timeout");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCancels_ReturnsCancelledFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new RecordingClient { Exception = new TaskCanceledException("caller cancelled") };
        var adapter = CreateAdapter(client);

        var events = await CollectAsync(adapter.ExecuteAsync(Request(), cancellation.Token));

        events[^1].Failure!.Kind.Should().Be(CodexExecutionFailureKind.Cancelled);
        events[^1].Failure!.Code.Should().Be("managed_execution_cancelled");
    }

    private static ChronoSandboxCodexExecutionAdapter CreateAdapter(
        RecordingClient client,
        ManagedCodexOptions? options = null) =>
        new(
            Options.Create(options ?? ManagedCodexOptionsValidatorTests.ValidOptions()),
            client,
            NullLogger<ChronoSandboxCodexExecutionAdapter>.Instance);

    private static CodexExecutionRequest Request() => new(
        new CodexExecutionTarget { ManagedSandbox = new CodexManagedSandboxTarget() },
        new CodexExecutionWorkspace { EmptyGit = new CodexEmptyGitWorkspace() },
        "Reply with exactly CODEX_EXEC_READY",
        180,
        new CodexExecutionCallerContext(
            "interactive-bearer-must-not-be-forwarded",
            new CodexExecutionNyxIdAuthority("nyxid", string.Empty, "user-a"),
            "user-a",
            "run-a",
            "step-a",
            "call-a"));

    private static async Task<List<CodexExecutionEvent>> CollectAsync(
        IAsyncEnumerable<CodexExecutionEvent> source)
    {
        var result = new List<CodexExecutionEvent>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }

    private sealed class RecordingClient : IChronoSandboxCodexClient
    {
        public int CallCount { get; private set; }
        public CodexExecutionRequest? Request { get; private set; }
        public CodexExecutionResult Result { get; init; } = new("unused");
        public Exception? Exception { get; init; }

        public Task<CodexExecutionResult> ExecuteAsync(
            CodexExecutionRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            Request = request;
            return Exception is null
                ? Task.FromResult(Result)
                : Task.FromException<CodexExecutionResult>(Exception);
        }
    }
}
