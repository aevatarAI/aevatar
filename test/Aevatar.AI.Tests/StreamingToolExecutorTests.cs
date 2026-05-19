using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class StreamingToolExecutorTests
{
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

        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "read1", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-2", Name = "read2", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-3", Name = "read3", ArgumentsJson = "{}" });

        await gate.WaitForEntrantsAsync(CancellationToken.None);
        gate.Release();

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
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

        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "write1", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-2", Name = "write2", ArgumentsJson = "{}" });

        await gate.WaitForEntrantsAsync(CancellationToken.None);
        executor.GetCompletedResults().Should().BeEmpty();
        gate.Release();

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
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

        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-slow", Name = "slow", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-fast", Name = "fast", ArgumentsJson = "{}" });

        await fastCompleted.Task;
        executor.GetCompletedResults().Should().BeEmpty();
        slowGate.Release();

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
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

        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "read1", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-2", Name = "read2", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-3", Name = "write1", ArgumentsJson = "{}" });

        await readsGate.WaitForEntrantsAsync(CancellationToken.None);
        executionLog.Should().NotContain("write1-start");
        readsGate.Release();
        await writeStarted.Task;

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
            results.Add(result);

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
        // Use middleware to simulate an exception that escapes past ToolManager's catch.
        // ToolManager itself catches exceptions, so we need middleware to throw instead.
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("failing", isReadOnly: false, _ => "ok"));
        tools.Register(new ConcurrencyTrackingTool("skipped", isReadOnly: false, _ => "should-not-run"));

        var throwOnFirst = true;
        var middleware = new DelegateToolCallMiddleware(async (ctx, next) =>
        {
            if (ctx.ToolName == "failing" && throwOnFirst)
            {
                throwOnFirst = false;
                throw new InvalidOperationException("boom");
            }
            await next();
        });

        using var executor = new StreamingToolExecutor(tools, toolMiddlewares: [middleware]);

        executor.AddTool(new ToolCall { Id = "tc-fail", Name = "failing", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-skip", Name = "skipped", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(2);
        results[0].CallId.Should().Be("tc-fail");
        results[0].IsError.Should().BeTrue();
        results[0].Result.Should().Contain("boom");
        results[1].CallId.Should().Be("tc-skip");
        results[1].IsError.Should().BeTrue();
        results[1].Result.Should().Contain("prior tool error");
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

        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "slow", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-2", Name = "queued", ArgumentsJson = "{}" });

        await slowGate.WaitForEntrantsAsync(CancellationToken.None);
        executor.Discard();
        slowGate.Release();

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.IsError);
    }

    [Fact]
    public async Task AddTool_AfterDiscard_ShouldReturnImmediateError()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, _ => "ok"));

        using var executor = new StreamingToolExecutor(tools);
        executor.Discard();

        executor.AddTool(new ToolCall { Id = "tc-late", Name = "echo", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(1);
        results[0].CallId.Should().Be("tc-late");
        results[0].IsError.Should().BeTrue();
        results[0].Result.Should().Contain("discarded");
    }

    [Fact]
    public async Task MetadataPropagation_ShouldSetAsyncLocalDuringExecution()
    {
        string? capturedMetadata = null;
        var tools = new ToolManager();
        tools.Register(new DelegateAgentTool("meta-check", _ =>
        {
            capturedMetadata = AgentToolRequestContext.TryGet("auth_token");
            return "ok";
        }));

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_token"] = "secret-123",
        };

        using var executor = new StreamingToolExecutor(
            tools, requestMetadata: metadata);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "meta-check", ArgumentsJson = "{}" });

        await foreach (var _ in executor.GetRemainingResultsAsync(CancellationToken.None)) { }

        capturedMetadata.Should().Be("secret-123");
        // Metadata should be cleared after execution
        AgentToolRequestContext.CurrentMetadata.Should().BeNull();
    }

    [Fact]
    public async Task GetCompletedResults_ShouldReturnNonBlocking()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, _ => "ok"));

        using var executor = new StreamingToolExecutor(tools);

        // Before adding any tools
        executor.GetCompletedResults().Should().BeEmpty();

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" });

        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
        {
            result.CallId.Should().Be("tc-1");
            result.Result.Should().Be("ok");
            break;
        }

        var results = executor.GetCompletedResults().ToList();
        results.Should().BeEmpty();

        // Should not yield again (already yielded)
        executor.GetCompletedResults().Should().BeEmpty();
    }

    [Fact]
    public async Task HooksAndMiddleware_ShouldFirePerTool()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, _ => "result"));

        var hook = new CountingHook();
        var hooks = new AgentHookPipeline([hook]);

        var middlewareCalls = 0;
        var middleware = new DelegateToolCallMiddleware(async (_, next) =>
        {
            Interlocked.Increment(ref middlewareCalls);
            await next();
        });

        using var executor = new StreamingToolExecutor(tools, hooks, [middleware]);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-2", Name = "echo", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(2);
        hook.ToolStartCount.Should().Be(2);
        hook.ToolEndCount.Should().Be(2);
        middlewareCalls.Should().Be(2);
    }

    [Fact]
    public async Task UnknownTool_ShouldReturnNotFoundResult()
    {
        var tools = new ToolManager();
        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "nonexistent", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(1);
        results[0].CallId.Should().Be("tc-1");
        results[0].Result.Should().Contain("not found");
    }

    [Fact]
    public async Task CoordinatorFault_ShouldSurfaceToCaller_NotHang()
    {
        // Fix (pr678-review): the coordinator loop was started fire-and-forget, so a fault
        // inside it left GetRemainingResultsAsync awaiting a completion that was never set.
        // A tool whose IsReadOnly getter throws forces the coordinator loop to fault; the
        // caller must observe that fault instead of hanging forever.
        var tools = new ToolManager();
        tools.Register(new FaultingTool("boom"));

        using var executor = new StreamingToolExecutor(tools);
        executor.AddTool(new ToolCall { Id = "tc-1", Name = "boom", ArgumentsJson = "{}" });

        // Safety net: with the fix the fault surfaces in milliseconds; without it this
        // await would hang indefinitely, so the linked timeout bounds a regression.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var drain = async () =>
        {
            await foreach (var _ in executor.GetRemainingResultsAsync(timeout.Token)) { }
        };

        (await drain.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("injected coordinator fault");
    }

    // ─── Test helpers ───

    private sealed class ConcurrencyTrackingTool : IAgentTool
    {
        private readonly Func<CancellationToken, Task<string>> _execute;

        public ConcurrencyTrackingTool(string name, bool isReadOnly, Func<CancellationToken, Task<string>> execute)
        {
            Name = name;
            IsReadOnly = isReadOnly;
            _execute = execute;
        }

        public ConcurrencyTrackingTool(string name, bool isReadOnly, Func<CancellationToken, string> execute)
            : this(name, isReadOnly, ct => Task.FromResult(execute(ct)))
        {
        }

        public string Name { get; }
        public string Description => "test";
        public string ParametersSchema => "{}";
        public bool IsReadOnly { get; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) => _execute(ct);
    }

    private sealed class DelegateAgentTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => "delegate";
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(execute(argumentsJson));
    }

    private sealed class FaultingTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => "faulting";
        public string ParametersSchema => "{}";
        // Read by the coordinator's HandleToolDiscovered — throwing here faults the loop.
        public bool IsReadOnly => throw new InvalidOperationException("injected coordinator fault");
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("unreachable");
    }

    private sealed class DelegateToolCallMiddleware(
        Func<ToolCallContext, Func<Task>, Task> handler) : IToolCallMiddleware
    {
        public Task InvokeAsync(ToolCallContext context, Func<Task> next) => handler(context, next);
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
}
