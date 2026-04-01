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
        var lockObj = new object();

        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("read1", isReadOnly: true, () =>
        {
            lock (lockObj) { concurrentCount++; maxConcurrent = Math.Max(maxConcurrent, concurrentCount); }
            Thread.Sleep(100);
            lock (lockObj) { concurrentCount--; }
            return "r1";
        }));
        tools.Register(new ConcurrencyTrackingTool("read2", isReadOnly: true, () =>
        {
            lock (lockObj) { concurrentCount++; maxConcurrent = Math.Max(maxConcurrent, concurrentCount); }
            Thread.Sleep(100);
            lock (lockObj) { concurrentCount--; }
            return "r2";
        }));
        tools.Register(new ConcurrencyTrackingTool("read3", isReadOnly: true, () =>
        {
            lock (lockObj) { concurrentCount++; maxConcurrent = Math.Max(maxConcurrent, concurrentCount); }
            Thread.Sleep(100);
            lock (lockObj) { concurrentCount--; }
            return "r3";
        }));

        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "read1", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-2", Name = "read2", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-3", Name = "read3", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
            results.Add(result);

        maxConcurrent.Should().BeGreaterThan(1, "read-only tools should execute concurrently");
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task NonReadOnlyTools_ShouldExecuteSerially()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("write1", isReadOnly: false, () =>
        {
            lock (lockObj) { concurrentCount++; maxConcurrent = Math.Max(maxConcurrent, concurrentCount); }
            Thread.Sleep(50);
            lock (lockObj) { concurrentCount--; }
            return "w1";
        }));
        tools.Register(new ConcurrencyTrackingTool("write2", isReadOnly: false, () =>
        {
            lock (lockObj) { concurrentCount++; maxConcurrent = Math.Max(maxConcurrent, concurrentCount); }
            Thread.Sleep(50);
            lock (lockObj) { concurrentCount--; }
            return "w2";
        }));

        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "write1", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-2", Name = "write2", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(CancellationToken.None))
            results.Add(result);

        maxConcurrent.Should().Be(1, "non-read-only tools should execute serially");
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task Results_ShouldBeYieldedInCallOrder_NotCompletionOrder()
    {
        var tools = new ToolManager();
        // First tool is slow, second is fast — results must still come in call order
        tools.Register(new ConcurrencyTrackingTool("slow", isReadOnly: true, () =>
        {
            Thread.Sleep(200);
            return "slow-result";
        }));
        tools.Register(new ConcurrencyTrackingTool("fast", isReadOnly: true, () =>
        {
            return "fast-result";
        }));

        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-slow", Name = "slow", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-fast", Name = "fast", ArgumentsJson = "{}" });

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
        var lockObj = new object();

        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("read1", isReadOnly: true, () =>
        {
            lock (lockObj) executionLog.Add("read1-start");
            Thread.Sleep(50);
            lock (lockObj) executionLog.Add("read1-end");
            return "r1";
        }));
        tools.Register(new ConcurrencyTrackingTool("read2", isReadOnly: true, () =>
        {
            lock (lockObj) executionLog.Add("read2-start");
            Thread.Sleep(50);
            lock (lockObj) executionLog.Add("read2-end");
            return "r2";
        }));
        tools.Register(new ConcurrencyTrackingTool("write1", isReadOnly: false, () =>
        {
            lock (lockObj) executionLog.Add("write1-start");
            Thread.Sleep(50);
            lock (lockObj) executionLog.Add("write1-end");
            return "w1";
        }));

        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "read1", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-2", Name = "read2", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-3", Name = "write1", ArgumentsJson = "{}" });

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
        tools.Register(new ConcurrencyTrackingTool("failing", isReadOnly: false, () => "ok"));
        tools.Register(new ConcurrencyTrackingTool("skipped", isReadOnly: false, () => "should-not-run"));

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
        tools.Register(new ConcurrencyTrackingTool("slow", isReadOnly: false, () =>
        {
            Thread.Sleep(500);
            return "done";
        }));
        tools.Register(new ConcurrencyTrackingTool("queued", isReadOnly: false, () => "q"));

        using var executor = new StreamingToolExecutor(tools);

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "slow", ArgumentsJson = "{}" });
        executor.AddTool(new ToolCall { Id = "tc-2", Name = "queued", ArgumentsJson = "{}" });

        // Give first tool a moment to start
        await Task.Delay(50);
        executor.Discard();

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
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, () => "ok"));

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
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, () => "ok"));

        using var executor = new StreamingToolExecutor(tools);

        // Before adding any tools
        executor.GetCompletedResults().Should().BeEmpty();

        executor.AddTool(new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" });

        // Wait for completion
        await Task.Delay(100);

        var results = executor.GetCompletedResults().ToList();
        results.Should().HaveCount(1);
        results[0].CallId.Should().Be("tc-1");
        results[0].Result.Should().Be("ok");

        // Should not yield again (already yielded)
        executor.GetCompletedResults().Should().BeEmpty();
    }

    [Fact]
    public async Task HooksAndMiddleware_ShouldFirePerTool()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, () => "result"));

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

    // ─── Test helpers ───

    private sealed class ConcurrencyTrackingTool : IAgentTool
    {
        private readonly Func<string> _execute;

        public ConcurrencyTrackingTool(string name, bool isReadOnly, Func<string> execute)
        {
            Name = name;
            IsReadOnly = isReadOnly;
            _execute = execute;
        }

        public string Name { get; }
        public string Description => "test";
        public string ParametersSchema => "{}";
        public bool IsReadOnly { get; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.Run(() => _execute(), ct);
        }
    }

    private sealed class DelegateAgentTool(string name, Func<string, string> execute) : IAgentTool
    {
        public string Name => name;
        public string Description => "delegate";
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(execute(argumentsJson));
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
}
