using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class StreamingToolExecutorTests
{
    [Fact]
    public void StreamingToolExecutorSource_ShouldNotOwnProcessLocalProgressCoordinator()
    {
        var root = FindRepositoryRoot();
        var source = StripLineComments(File.ReadAllText(Path.Combine(
            root,
            "src",
            "Aevatar.AI.Core",
            "Tools",
            "StreamingToolExecutor.cs")));

        source.Should().NotContain("System.Threading.Channels");
        source.Should().NotContain("Channel<");
        source.Should().NotContain("TaskCompletionSource");
        source.Should().NotContain("private readonly List<ToolExecutionEntry>");
        source.Should().NotContain("private readonly List<TaskCompletionSource>");
        source.Should().NotContain("private readonly ExecutionState");
    }

    [Fact]
    public async Task ReadOnlyTools_ShouldExecuteInParallel()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var gate = new ToolExecutionGate(expectedEntrants: 3);

        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("read1", isReadOnly: true, async ct =>
            await TrackConcurrencyAsync("r1", gate, ct)));
        tools.Register(new ConcurrencyTrackingTool("read2", isReadOnly: true, async ct =>
            await TrackConcurrencyAsync("r2", gate, ct)));
        tools.Register(new ConcurrencyTrackingTool("read3", isReadOnly: true, async ct =>
            await TrackConcurrencyAsync("r3", gate, ct)));

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-1", Name = "read1", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-2", Name = "read2", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-3", Name = "read3", ArgumentsJson = "{}" });

        await gate.WaitForEntrantsAsync(CancellationToken.None);
        gate.Release();

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        maxConcurrent.Should().BeGreaterThan(1, "read-only tools should execute concurrently");
        results.Should().HaveCount(3);
        return;

        async Task<string> TrackConcurrencyAsync(string result, ToolExecutionGate executionGate, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref concurrentCount);
            UpdateMaxConcurrent(ref maxConcurrent, current);
            executionGate.SignalEntered();
            await executionGate.WaitForReleaseAsync(ct);
            Interlocked.Decrement(ref concurrentCount);
            return result;
        }
    }

    [Fact]
    public async Task NonReadOnlyTools_ShouldExecuteSerially()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var gate = new ToolExecutionGate(expectedEntrants: 1);

        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("write1", isReadOnly: false, async ct =>
            await TrackConcurrencyAsync("w1", ct)));
        tools.Register(new ConcurrencyTrackingTool("write2", isReadOnly: false, async ct =>
            await TrackConcurrencyAsync("w2", ct)));

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-1", Name = "write1", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-2", Name = "write2", ArgumentsJson = "{}" });

        await gate.WaitForEntrantsAsync(CancellationToken.None);
        executor.GetCompletedResults(executionState).Should().BeEmpty();
        gate.Release();

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        maxConcurrent.Should().Be(1, "non-read-only tools should execute serially");
        results.Should().HaveCount(2);
        return;

        async Task<string> TrackConcurrencyAsync(string result, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref concurrentCount);
            UpdateMaxConcurrent(ref maxConcurrent, current);
            if (result == "w1")
            {
                gate.SignalEntered();
                await gate.WaitForReleaseAsync(ct);
            }
            Interlocked.Decrement(ref concurrentCount);
            return result;
        }
    }

    [Fact]
    public async Task Results_ShouldBeYieldedInCallOrder_NotCompletionOrder()
    {
        var tools = new ToolManager();
        var slowGate = new ToolExecutionGate(expectedEntrants: 1);
        var fastCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // First tool is slow, second is fast — results must still come in call order
        tools.Register(new ConcurrencyTrackingTool("slow", isReadOnly: true, async ct =>
        {
            slowGate.SignalEntered();
            await slowGate.WaitForReleaseAsync(ct);
            return "slow-result";
        }));
        tools.Register(new ConcurrencyTrackingTool("fast", isReadOnly: true, _ =>
        {
            fastCompleted.TrySetResult();
            return "fast-result";
        }));

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-slow", Name = "slow", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-fast", Name = "fast", ArgumentsJson = "{}" });

        await fastCompleted.Task;
        executor.GetCompletedResults(executionState).Should().BeEmpty();
        slowGate.Release();

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(2);
        results[0].CallId.Should().Be("tc-slow", "first added tool should be yielded first");
        results[1].CallId.Should().Be("tc-fast");
    }

    [Fact]
    public async Task MixedTools_ShouldRespectConcurrencyBoundaries()
    {
        var executionLog = new List<string>();
        var readsGate = new ToolExecutionGate(expectedEntrants: 2);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("read1", isReadOnly: true, async ct =>
        {
            executionLog.Add("read1-start");
            readsGate.SignalEntered();
            await readsGate.WaitForReleaseAsync(ct);
            executionLog.Add("read1-end");
            return "r1";
        }));
        tools.Register(new ConcurrencyTrackingTool("read2", isReadOnly: true, async ct =>
        {
            executionLog.Add("read2-start");
            readsGate.SignalEntered();
            await readsGate.WaitForReleaseAsync(ct);
            executionLog.Add("read2-end");
            return "r2";
        }));
        tools.Register(new ConcurrencyTrackingTool("write1", isReadOnly: false, _ =>
        {
            executionLog.Add("write1-start");
            writeStarted.TrySetResult();
            executionLog.Add("write1-end");
            return "w1";
        }));

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-1", Name = "read1", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-2", Name = "read2", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-3", Name = "write1", ArgumentsJson = "{}" });

        await readsGate.WaitForEntrantsAsync(CancellationToken.None);
        executionLog.Should().NotContain("write1-start");
        readsGate.Release();

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        writeStarted.Task.IsCompleted.Should().BeTrue();
        results.Should().HaveCount(3);
        results[0].CallId.Should().Be("tc-1");
        results[1].CallId.Should().Be("tc-2");
        results[2].CallId.Should().Be("tc-3");

        // Write tool must start after both reads finish
        var writeStartIdx = executionLog.IndexOf("write1-start");
        var read1EndIdx = executionLog.IndexOf("read1-end");
        var read2EndIdx = executionLog.IndexOf("read2-end");
        writeStartIdx.Should().BeGreaterThan(read1EndIdx, "write should start after read1 ends");
        writeStartIdx.Should().BeGreaterThan(read2EndIdx, "write should start after read2 ends");
    }

    [Fact]
    public async Task ErrorCascading_ShouldSkipSubsequentQueuedTools()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("failing", isReadOnly: false, _ => "ok"));
        tools.Register(new ConcurrencyTrackingTool("skipped", isReadOnly: false, _ => "should-not-run"));

        var executor = NewStreamingToolExecutor(
            tools,
            toolExecutionPort: new ThrowingExecutionPort());
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-fail", Name = "failing", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-skip", Name = "skipped", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(2);
        results[0].CallId.Should().Be("tc-fail");
        results[0].IsError.Should().BeTrue();
        results[0].Result.Should().NotContain("boom");
        results[0].Result.Should().Contain("The tool request failed.");
        results[0].Receipt.Should().NotBeNull();
        results[0].Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        results[1].CallId.Should().Be("tc-skip");
        results[1].IsError.Should().BeTrue();
        results[1].Result.Should().Contain("prior tool error");
        results[1].Receipt.Should().NotBeNull();
        results[1].Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
    }

    [Fact]
    public async Task Discard_ShouldCancelQueuedTools()
    {
        var tools = new ToolManager();
        var slowGate = new ToolExecutionGate(expectedEntrants: 1);
        tools.Register(new ConcurrencyTrackingTool("slow", isReadOnly: false, async ct =>
        {
            slowGate.SignalEntered();
            await slowGate.WaitForReleaseAsync(ct);
            return "done";
        }));
        tools.Register(new ConcurrencyTrackingTool("queued", isReadOnly: false, _ => "q"));

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-1", Name = "slow", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-2", Name = "queued", ArgumentsJson = "{}" });

        await slowGate.WaitForEntrantsAsync(CancellationToken.None);
        executor.Discard(executionState);
        slowGate.Release();

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.IsError);
    }

    [Fact]
    public async Task AddTool_AfterDiscard_ShouldReturnImmediateError()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, _ => "ok"));

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();
        executor.Discard(executionState);

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-late", Name = "echo", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(1);
        results[0].CallId.Should().Be("tc-late");
        results[0].IsError.Should().BeTrue();
        results[0].Result.Should().Contain("discarded");
    }

    [Fact]
    public async Task ExecutionStates_OnSameExecutor_ShouldIsolateQueuesResultsAndDiscard()
    {
        var tools = new ToolManager();
        var firstGate = new ToolExecutionGate(expectedEntrants: 1);
        tools.Register(new ConcurrencyTrackingTool("blocked", isReadOnly: true, async ct =>
        {
            firstGate.SignalEntered();
            await firstGate.WaitForReleaseAsync(ct);
            return "blocked-result";
        }));
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, _ => "ok"));

        var executor = NewStreamingToolExecutor(tools);
        using var firstState = executor.CreateExecutionState();
        using var secondState = executor.CreateExecutionState();

        await AddToolAsync(executor, firstState, new ToolCall { Id = "first", Name = "blocked", ArgumentsJson = "{}" });
        await AddToolAsync(executor, secondState, new ToolCall { Id = "second", Name = "echo", ArgumentsJson = "{}" });
        await firstGate.WaitForEntrantsAsync(CancellationToken.None);
        executor.Discard(firstState);
        firstGate.Release();

        var firstResults = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(firstState, CancellationToken.None))
            firstResults.Add(result);

        var secondResults = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(secondState, CancellationToken.None))
            secondResults.Add(result);

        firstResults.Should().ContainSingle();
        firstResults[0].CallId.Should().Be("first");
        firstResults[0].IsError.Should().BeTrue();
        firstResults[0].Result.Should().Contain("discarded");

        secondResults.Should().ContainSingle();
        secondResults[0].CallId.Should().Be("second");
        secondResults[0].IsError.Should().BeFalse();
        secondResults[0].Result.Should().Be("ok");
        executor.GetCompletedResults(firstState).Should().BeEmpty();
        executor.GetCompletedResults(secondState).Should().BeEmpty();
    }

    [Fact]
    public async Task TypedContextPropagation_ShouldSetAsyncLocalDuringExecutionAndRestore()
    {
        string? capturedToken = null;
        string? capturedExternal = null;
        string? capturedCallId = null;
        var tools = new ToolManager();
        tools.Register(new DelegateAgentTool("meta-check", _ =>
        {
            capturedToken = AgentToolRequestContext.NyxIdAccessToken;
            capturedExternal = AgentToolRequestContext.TryGetExternalMetadata("auth_token");
            capturedCallId = AgentToolRequestContext.CallId;
            return "ok";
        }));

        var toolContext = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("typed-secret", null, null),
            ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["auth_token"] = "secret-123",
            },
        };

        var executor = NewStreamingToolExecutor(
            tools, toolContext: toolContext);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-1", Name = "meta-check", ArgumentsJson = "{}" });

        await foreach (var _ in executor.GetRemainingResultsAsync(executionState, CancellationToken.None)) { }

        capturedToken.Should().Be("typed-secret");
        capturedExternal.Should().Be("secret-123");
        capturedCallId.Should().Be("tc-1");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task RequestMetadata_ShouldNotPromoteOwnedControlKeysDuringToolExecution()
    {
        string? capturedToken = null;
        string? capturedExternal = null;
        string? capturedCallId = null;
        var tools = new ToolManager();
        tools.Register(new DelegateAgentTool("meta-check", _ =>
        {
            capturedToken = AgentToolRequestContext.NyxIdAccessToken;
            capturedExternal = AgentToolRequestContext.TryGetExternalMetadata("auth_token");
            capturedCallId = AgentToolRequestContext.CallId;
            return "ok";
        }));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-secret",
            [LLMRequestMetadataKeys.CallId] = "metadata-call",
            ["auth_token"] = "secret-123",
        };

        var executor = NewStreamingToolExecutor(
            tools,
            requestMetadata: metadata);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-1", Name = "meta-check", ArgumentsJson = "{}" });

        await foreach (var _ in executor.GetRemainingResultsAsync(executionState, CancellationToken.None)) { }

        capturedToken.Should().BeNull();
        capturedExternal.Should().Be("secret-123");
        capturedCallId.Should().Be("tc-1");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task TypedContext_ShouldTakePrecedenceOverRequestMetadata()
    {
        string? capturedToken = null;
        string? capturedExternal = null;
        string? capturedCallId = null;
        var tools = new ToolManager();
        tools.Register(new DelegateAgentTool("meta-check", _ =>
        {
            capturedToken = AgentToolRequestContext.NyxIdAccessToken;
            capturedExternal = AgentToolRequestContext.TryGetExternalMetadata("trace-id");
            capturedCallId = AgentToolRequestContext.CallId;
            return "ok";
        }));

        var typedContext = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("typed-token", null, null),
            ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["trace-id"] = "typed-trace",
            },
        };
        var requestMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-token",
            ["trace-id"] = "metadata-trace",
        };
        var executor = NewStreamingToolExecutor(
            tools,
            requestMetadata: requestMetadata,
            toolContext: typedContext);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-typed", Name = "meta-check", ArgumentsJson = "{}" });

        await foreach (var _ in executor.GetRemainingResultsAsync(executionState, CancellationToken.None)) { }

        capturedToken.Should().Be("typed-token");
        capturedExternal.Should().Be("typed-trace");
        capturedCallId.Should().Be("tc-typed");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task NullRequestMetadataAndContext_ShouldCreateOnlyPreparedOperationIdentity()
    {
        AgentToolExecutionContext? capturedContext = AgentToolExecutionContext.Empty;
        var tools = new ToolManager();
        tools.Register(new DelegateAgentTool("meta-check", _ =>
        {
            capturedContext = AgentToolRequestContext.Current;
            return "ok";
        }));

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-1", Name = "meta-check", ArgumentsJson = "{}" });

        await foreach (var _ in executor.GetRemainingResultsAsync(executionState, CancellationToken.None)) { }

        capturedContext.Should().NotBeNull();
        capturedContext!.Request.CallId.Should().Be("tc-1");
        capturedContext.Request.OperationId.Should().NotBeNullOrWhiteSpace();
        capturedContext.Credentials.Should().Be(AgentToolCredentials.Empty);
        capturedContext.ExternalMetadata.Should().BeEmpty();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task GetCompletedResults_ShouldReturnNonBlocking()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, _ => "ok"));

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        // Before adding any tools
        executor.GetCompletedResults(executionState).Should().BeEmpty();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" });

        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
        {
            result.CallId.Should().Be("tc-1");
            result.Result.Should().Be("ok");
            break;
        }

        var results = executor.GetCompletedResults(executionState).ToList();
        results.Should().BeEmpty();

        // Should not yield again (already yielded)
        executor.GetCompletedResults(executionState).Should().BeEmpty();
    }

    [Fact]
    public async Task Hooks_ShouldFirePerTool()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, _ => "result"));

        var hook = new CountingHook();
        var hooks = new AgentHookPipeline([hook]);

        var executor = NewStreamingToolExecutor(tools, hooks);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-2", Name = "echo", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(2);
        hook.ToolStartCount.Should().Be(2);
        hook.ToolEndCount.Should().Be(2);
    }

    [Fact]
    public async Task HookRewrite_FromReadOnlyToNonReadOnly_ShouldErrorAndSkipQueuedTool()
    {
        var destructiveRan = false;
        var skippedRan = false;
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("read", isReadOnly: true, _ => "read-result"));
        tools.Register(new ConcurrencyTrackingTool("write", isReadOnly: false, _ =>
        {
            destructiveRan = true;
            return "write-result";
        }));
        tools.Register(new ConcurrencyTrackingTool("queued", isReadOnly: false, _ =>
        {
            skippedRan = true;
            return "queued-result";
        }));

        var hooks = new AgentHookPipeline([new RewriteToolNameHook("read", "write")]);
        var executor = NewStreamingToolExecutor(tools, hooks);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-rewrite", Name = "read", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-queued", Name = "queued", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(2);
        results[0].CallId.Should().Be("tc-rewrite");
        results[0].IsError.Should().BeTrue();
        results[0].Result.Should().Contain(
            "A prepared tool operation cannot be rewritten after its intent is committed.");
        results[1].CallId.Should().Be("tc-queued");
        results[1].IsError.Should().BeTrue();
        results[1].Result.Should().Contain("prior tool error");
        destructiveRan.Should().BeFalse("rewritten non-read-only tool must not execute after concurrent admission");
        skippedRan.Should().BeFalse("scheduler fault should prevent queued tools from executing");
    }

    [Fact]
    public async Task UnknownTool_ShouldReturnSafeErrorResult()
    {
        var tools = new ToolManager();
        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-1", Name = "nonexistent", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(1);
        results[0].CallId.Should().Be("tc-1");
        results[0].IsError.Should().BeTrue();
        results[0].Result.Should().Contain("The tool request failed.");
        results[0].Receipt.Should().NotBeNull();
        results[0].Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
    }

    [Fact]
    public async Task UnauthorizedTool_ShouldReturnNotAvailableWithoutExecutingTool()
    {
        var executed = false;
        var tools = new ToolManager();
        tools.Register(new DelegateAgentTool("blocked", _ =>
        {
            executed = true;
            return "{}";
        }));
        var executor = NewStreamingToolExecutor(
            tools,
            toolContext: AgentToolExecutionContext.Empty with
            {
                ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(["allowed"]),
            });
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-blocked", Name = "blocked", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().ContainSingle();
        results[0].CallId.Should().Be("tc-blocked");
        results[0].IsError.Should().BeTrue();
        results[0].Result.Should().Contain("not available");
        executed.Should().BeFalse();
    }

    [Fact]
    public async Task HookRewrite_ToUnauthorizedTool_ShouldReturnNotAvailable()
    {
        var tools = new ToolManager();
        tools.Register(new DelegateAgentTool("allowed", _ => "{}"));
        tools.Register(new DelegateAgentTool("blocked", _ => """{"blocked":true}"""));
        var hooks = new AgentHookPipeline([new RewriteToolNameHook("allowed", "blocked")]);
        var executor = NewStreamingToolExecutor(
            tools,
            hooks,
            toolContext: AgentToolExecutionContext.Empty with
            {
                ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(["allowed"]),
            });
        using var executionState = executor.CreateExecutionState();

        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-rewrite", Name = "allowed", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().ContainSingle();
        results[0].CallId.Should().Be("tc-rewrite");
        results[0].IsError.Should().BeTrue();
        results[0].Result.Should().Contain("not available");
    }

    [Fact]
    public async Task ProviderSuccessReceipts_ShouldPreserveSafetyFacts()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("plain-write", isReadOnly: false, _ => """{"ok":true}"""));
        tools.Register(new ConcurrencyTrackingTool("approval", isReadOnly: true, _ => """{"ok":true}""")
        {
            ApprovalMode = ToolApprovalMode.AlwaysRequire,
        });
        tools.Register(new ConcurrencyTrackingTool("destructive", isReadOnly: true, _ => """{"ok":true}""")
        {
            IsDestructive = true,
        });
        tools.Register(new ConcurrencyTrackingTool("side-effect", isReadOnly: true, _ => """{"id":"side-1"}""")
        {
            SideEffectKind = "Example.Publish",
        });

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-write", Name = "plain-write", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-approval", Name = "approval", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-destructive", Name = "destructive", ArgumentsJson = "{}" });
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-side", Name = "side-effect", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(4);
        var plainWriteReceipt = results[0].Receipt;
        plainWriteReceipt.Should().NotBeNull();
        plainWriteReceipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        plainWriteReceipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.NeverRequire);
        plainWriteReceipt.IsDestructive.Should().BeFalse();
        plainWriteReceipt.SideEffectKind.Should().BeEmpty();
        var approvalReceipt = results[1].Receipt;
        approvalReceipt.Should().NotBeNull();
        approvalReceipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        approvalReceipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.AlwaysRequire);
        var destructiveReceipt = results[2].Receipt;
        destructiveReceipt.Should().NotBeNull();
        destructiveReceipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        destructiveReceipt!.IsDestructive.Should().BeTrue();
        var sideEffectReceipt = results[3].Receipt;
        sideEffectReceipt.Should().NotBeNull();
        sideEffectReceipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        sideEffectReceipt!.SideEffectKind.Should().Be("example.publish");
    }

    [Fact]
    public async Task ProviderExecutionOutcomeReceipt_ShouldFlowWithoutResultReclassification()
    {
        var tools = new ToolManager();
        tools.Register(new ExecutionOutcomeReceiptTool());
        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();
        await AddToolAsync(executor, executionState, new ToolCall
        {
            Id = "tc-outcome",
            Name = "execution_outcome",
            ArgumentsJson = "{}",
        });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        var completed = results.Should().ContainSingle().Which;
        completed.IsError.Should().BeFalse();
        completed.Result.Should().Be("""{"error":true,"status":503,"body":"domain payload"}""");
        completed.Receipt.Should().NotBeNull();
        completed.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        completed.Receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.Auto);
        completed.Receipt.IsDestructive.Should().BeTrue();
        completed.Receipt.SideEffectKind.Should().Be("example.publish");
        completed.Receipt.SubjectId.Should().Be("usvc-outcome");
    }

    [Fact]
    public async Task ErrorJsonWithoutReceipt_ShouldEmitUnknownFailure()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool(
            "ornn_search_skills",
            isReadOnly: true,
            _ => """{"error":true,"status":503}""",
            emitSuccessReceipt: false)
        {
            SideEffectKind = "",
        });

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-search", Name = "ornn_search_skills", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().ContainSingle();
        results[0].IsError.Should().BeTrue();
        results[0].Result.Should().Be(
            """{"status":"unknown","message":"The tool outcome could not be verified."}""");
        var receipt = results[0].Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Unspecified);
        receipt.ResultJson.Should().Be(results[0].Result);
        receipt.ErrorCode.Should().Be("tool_outcome_unknown");
        receipt.ErrorMessage.Should().Be("The tool outcome could not be verified.");
    }

    [Fact]
    public async Task ReceiptWorthyOrnnPublishResult_ShouldUseToolSuppliedSubjectReceipt()
    {
        var tools = new ToolManager();
        tools.Register(new OrnnPublishSubjectReceiptTool(
            """{"id":"skill-2","version":"1.0","hash":"hash-2"}"""));

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();
        await AddToolAsync(executor, executionState, new ToolCall
        {
            Id = "tc-publish",
            Name = "ornn_publish_skill",
            ArgumentsJson = "{}",
        });

        var results = new List<ToolExecutionResult>();
        await foreach (var completion in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(completion);

        results.Should().ContainSingle();
        var receipt = results[0].Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.SideEffectKind.Should().Be("ornn.publish.skill");
        receipt.SubjectKind.Should().Be("ornn.skill");
        receipt.SubjectId.Should().Be("skill-2");
        receipt.SubjectVersion.Should().Be("1.0");
        receipt.SubjectHash.Should().Be("hash-2");
    }

    [Fact]
    public async Task ToolExecutionError_ShouldEmitErrorReceiptForReceiptWorthyTool()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("publish", isReadOnly: true, _ => Task.FromException<string>(new InvalidOperationException("boom")))
        {
            SideEffectKind = "ornn.publish.skill",
        });

        var executor = NewStreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();
        await AddToolAsync(executor, executionState, new ToolCall { Id = "tc-publish", Name = "publish", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().ContainSingle();
        results[0].IsError.Should().BeTrue();
        var receipt = results[0].Receipt;
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("tool_execution_exception");
        receipt.ErrorMessage.Should().Be(nameof(InvalidOperationException));
        receipt.ResultJson.Should().NotContain("boom");
    }

    [Fact]
    public async Task MissingTool_ShouldProduceAuditedTerminalFailureWithoutRawException()
    {
        var tools = new ToolManager();
        var executionPort = new RecordingAdmittedExecutionPort();
        var executor = NewStreamingToolExecutor(
            tools,
            toolContext: AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-missing", null),
                ExecutionOwner = AgentToolExecutionOwners.HostService(nameof(StreamingToolExecutorTests)),
            },
            toolExecutionPort: executionPort);
        using var executionState = executor.CreateExecutionState();
        await AddToolAsync(executor, executionState, new ToolCall
        {
            Id = "tc-missing",
            Name = "missing_tool",
            ArgumentsJson = "{}",
        });

        var results = new List<ToolExecutionResult>();
        await foreach (var completion in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(completion);

        var result = results.Should().ContainSingle().Which;
        result.IsError.Should().BeTrue();
        var outcome = executionPort.Outcomes.Should().ContainSingle().Which;
        outcome.Kind.Should().Be(AgentToolExecutionOutcomeKind.Failed);
        outcome.FailureCode.Should().Be("tool_execution_exception");
        outcome.SafeMessage.Should().Be(nameof(InvalidOperationException));
        outcome.FailureStage.Should().Be(AgentToolExecutionFailureStage.TerminalExecution);
        outcome.TerminalInvoked.Should().BeTrue();
        outcome.Retryable.Should().BeFalse();
        outcome.AuditCompleted.Should().BeTrue();
        outcome.ResultJson.Should().NotContain("was not found");
        outcome.Receipt.ResultJson.Should().Be(outcome.ResultJson);
        executionPort.Records.Select(record => record.ToolExecution.ExecutionPhase)
            .Should().Equal(AuditToolExecutionPhase.Running, AuditToolExecutionPhase.Terminal);
    }

    [Fact]
    public async Task ProviderFailureReceipt_ShouldReplaceRawToolResultWithSafeResult()
    {
        var tools = new ToolManager();
        var tool = new SafeFailureReceiptTool();
        var executionPort = new TestExecutionPort();
        tools.Register(tool);
        var executor = NewStreamingToolExecutor(tools, toolExecutionPort: executionPort);
        using var executionState = executor.CreateExecutionState();
        await AddToolAsync(executor, executionState, new ToolCall
        {
            Id = "tc-safe-failure",
            Name = "safe_failure",
            ArgumentsJson = "{}",
        });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        var failure = results.Should().ContainSingle().Which;
        failure.IsError.Should().BeTrue();
        failure.Result.Should().Be("""{"error":"SAFE_FAILURE","message":"The tool request failed."}""");
        failure.ToString().Should().NotContain("bearer-secret").And.NotContain("credential");
        failure.Receipt.Should().NotBeNull();
        failure.Receipt!.ResultJson.Should().Be(failure.Result);
        executionPort.ExecutionCount.Should().Be(1);
        tool.ExecutionCount.Should().Be(1);
    }

    // ─── Test helpers ───

    private static async Task AddToolAsync(
        StreamingToolExecutor executor,
        StreamingToolExecutor.ExecutionState state,
        ToolCall toolCall)
    {
        var prepared = await executor.PrepareBatchAsync(
            "streaming-tool-executor-tests",
            round: 0,
            [toolCall]);
        executor.AddTool(state, prepared.Single());
    }

    private static StreamingToolExecutor NewStreamingToolExecutor(
        ToolManager tools,
        AgentHookPipeline? hooks = null,
        IReadOnlyDictionary<string, string>? requestMetadata = null,
        AgentToolExecutionContext? toolContext = null,
        IAgentToolExecutionPort? toolExecutionPort = null,
        AgentToolApprovalContinuationMode approvalContinuationMode = AgentToolApprovalContinuationMode.None) =>
        new(
            tools,
            hooks,
            requestMetadata,
            toolContext,
            toolExecutionPort ?? new TestExecutionPort(),
            checkpointPort: null,
            approvalContinuationMode);

    private sealed class ConcurrencyTrackingTool : IAgentTool
    {
        private readonly Func<CancellationToken, Task<string>> _execute;

        public ConcurrencyTrackingTool(
            string name,
            bool isReadOnly,
            Func<CancellationToken, Task<string>> execute,
            bool emitSuccessReceipt = true)
        {
            Name = name;
            IsReadOnly = isReadOnly;
            _execute = execute;
            EmitSuccessReceipt = emitSuccessReceipt;
        }

        public ConcurrencyTrackingTool(
            string name,
            bool isReadOnly,
            Func<CancellationToken, string> execute,
            bool emitSuccessReceipt = true)
            : this(name, isReadOnly, ct => Task.FromResult(execute(ct)), emitSuccessReceipt)
        {
        }

        public string Name { get; }
        public string Description => "test";
        public string ParametersSchema => "{}";
        public bool IsReadOnly { get; }
        public ToolApprovalMode ApprovalMode { get; init; } = ToolApprovalMode.NeverRequire;
        public bool IsDestructive { get; init; }
        public string SideEffectKind { get; init; } = "";
        private bool EmitSuccessReceipt { get; }

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            EmitSuccessReceipt
                ? new AgentToolReceipt
                {
                    CallId = callId,
                    ToolName = toolName,
                    Status = AgentToolReceiptStatus.Success,
                    ResultJson = resultJson,
                }
                : null;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) => _execute(ct);
    }

    private sealed class OrnnPublishSubjectReceiptTool(string resultJson) : IAgentTool
    {
        public string Name => "ornn_publish_skill";
        public string Description => "publish fixture";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
        public string SideEffectKind => "ornn.publish.skill";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(resultJson);

        public AgentToolReceipt? CreateSuccessReceipt(string callId, string toolName, string successResultJson)
        {
            using var document = System.Text.Json.JsonDocument.Parse(successResultJson);
            var root = document.RootElement;
            return new AgentToolReceipt
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ApprovalMode = AgentToolReceiptApprovalMode.Auto,
                SideEffectKind = SideEffectKind,
                SubjectKind = "ornn.skill",
                SubjectId = root.GetProperty("id").GetString() ?? string.Empty,
                SubjectVersion = root.GetProperty("version").GetString() ?? string.Empty,
                SubjectHash = root.GetProperty("hash").GetString() ?? string.Empty,
                ResultJson = successResultJson,
            };
        }
    }

    private sealed class ExecutionOutcomeReceiptTool : IAgentTool
    {
        public string Name => "execution_outcome";
        public string Description => "typed outcome fixture";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
        public bool IsDestructive => true;
        public string SideEffectKind => "Example.Publish";

        public Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
            string callId,
            string toolName,
            string argumentsJson,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentToolTerminalOutcome(
                """{"error":true,"status":503,"body":"domain payload"}""",
                new AgentToolReceipt
                {
                    CallId = callId,
                    ToolName = toolName,
                    Status = AgentToolReceiptStatus.Success,
                    SubjectKind = "nyxid.user-service",
                    SubjectId = "usvc-outcome",
                }));

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            throw new InvalidOperationException("The typed execution path must be used.");
    }

    private sealed class DelegateAgentTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => "delegate";
        public string ParametersSchema => "{}";
        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(execute(argumentsJson));
    }

    private sealed class SafeFailureReceiptTool : IAgentTool
    {
        public string Name => "safe_failure";
        public string Description => "safe failure fixture";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

        public int ExecutionCount { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult("""{"error":"forbidden","message":"credential bearer-secret rejected"}""");
        }

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = "SAFE_FAILURE",
                ErrorMessage = "The tool request failed.",
                ResultJson = """{"error":"SAFE_FAILURE","message":"The tool request failed."}""",
            };
    }

    private sealed class TestExecutionPort : IAgentToolExecutionPort
    {
        public int ExecutionCount { get; private set; }

        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ExecutionCount++;
            var safety = request.Tool.GetCallSafety(request.ArgumentsJson)
                ?? new AgentToolCallSafety(true, false, true);
            try
            {
                var terminalOutcome = await request.Tool.ExecuteWithOutcomeAsync(
                    request.ExecutionContext.Request.CallId ?? string.Empty,
                    request.Tool.Name,
                    request.ArgumentsJson,
                    ct);
                var result = terminalOutcome.ResultJson;
                var receipt = AgentToolReceiptFactory.CreateResult(
                    request.Tool,
                    request.ExecutionContext.Request.CallId ?? string.Empty,
                    request.Tool.Name,
                    safety,
                    result,
                    terminalOutcome.Receipt,
                    request.ArgumentsJson);
                return new AgentToolExecutionOutcome(
                    AgentToolExecutionOutcomeKind.Executed,
                    result,
                    receipt,
                    !safety.IsReadOnly,
                    string.Empty,
                    string.Empty,
                    AgentToolExecutionFailureStage.None,
                    TerminalInvoked: true,
                    Retryable: false,
                    AuditCompleted: true);
            }
            catch (Exception ex)
            {
                var result = ToolManager.BuildErrorJson("The tool request failed.");
                var receipt = AgentToolReceiptFactory.CreateError(
                    request.Tool,
                    request.ExecutionContext.Request.CallId ?? string.Empty,
                    request.Tool.Name,
                    safety,
                    result,
                    "tool_execution_exception",
                    ex.GetType().Name);
                return new AgentToolExecutionOutcome(
                    AgentToolExecutionOutcomeKind.Failed,
                    result,
                    receipt,
                    !safety.IsReadOnly,
                    "tool_execution_exception",
                    ex.GetType().Name,
                    AgentToolExecutionFailureStage.TerminalExecution,
                    TerminalInvoked: true,
                    Retryable: false,
                    AuditCompleted: true);
            }
        }
    }

    private sealed class RecordingAdmittedExecutionPort : IAgentToolExecutionPort
    {
        private readonly RecordingAuditTrailAppender _appender = new();
        private readonly AdmittedAgentToolExecutor _inner;

        public RecordingAdmittedExecutionPort()
        {
            _inner = new AdmittedAgentToolExecutor(
                AlwaysStartingAgentToolAdmissionLedger.Instance,
                _appender,
                new StableIdentityHasher());
        }

        public List<AgentToolExecutionOutcome> Outcomes { get; } = [];
        public IReadOnlyList<AuditRecord> Records => _appender.Records;

        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            var outcome = await _inner.ExecuteAsync(request, ct);
            Outcomes.Add(outcome);
            return outcome;
        }
    }

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
        }
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    private sealed class ThrowingExecutionPort : IAgentToolExecutionPort
    {
        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class CountingHook : IAIGAgentExecutionHook
    {
        public string Name => "counting";
        public int Priority => 0;
        public int ToolStartCount;
        public int ToolEndCount;

        public Task OnLLMRequestStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task OnLLMRequestEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task OnToolExecuteStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref ToolStartCount);
            return Task.CompletedTask;
        }

        public Task OnToolExecuteEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref ToolEndCount);
            return Task.CompletedTask;
        }
    }

    private sealed class RewriteToolNameHook(string fromName, string toName) : IAIGAgentExecutionHook
    {
        public string Name => "rewrite-tool";
        public int Priority => 0;

        public Task OnToolExecuteStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            if (string.Equals(ctx.ToolName, fromName, StringComparison.OrdinalIgnoreCase))
                ctx.ToolName = toName;

            return Task.CompletedTask;
        }
    }

    private sealed class ToolExecutionGate(int expectedEntrants)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entrants;

        public void SignalEntered()
        {
            if (Interlocked.Increment(ref _entrants) >= expectedEntrants)
                _entered.TrySetResult();
        }

        public Task WaitForEntrantsAsync(CancellationToken ct) => _entered.Task.WaitAsync(ct);

        public Task WaitForReleaseAsync(CancellationToken ct) => _release.Task.WaitAsync(ct);

        public void Release() => _release.TrySetResult();
    }

    private static void UpdateMaxConcurrent(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current)
                return;

            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "aevatar.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string StripLineComments(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0)
                lines[i] = lines[i][..idx];
        }

        return string.Join('\n', lines);
    }
}
