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

        var executor = new StreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-1", Name = "read1", ArgumentsJson = "{}" });
        executor.AddTool(executionState, new ToolCall { Id = "tc-2", Name = "read2", ArgumentsJson = "{}" });
        executor.AddTool(executionState, new ToolCall { Id = "tc-3", Name = "read3", ArgumentsJson = "{}" });

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

        var executor = new StreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-1", Name = "write1", ArgumentsJson = "{}" });
        executor.AddTool(executionState, new ToolCall { Id = "tc-2", Name = "write2", ArgumentsJson = "{}" });

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

        var executor = new StreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-slow", Name = "slow", ArgumentsJson = "{}" });
        executor.AddTool(executionState, new ToolCall { Id = "tc-fast", Name = "fast", ArgumentsJson = "{}" });

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

        var executor = new StreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-1", Name = "read1", ArgumentsJson = "{}" });
        executor.AddTool(executionState, new ToolCall { Id = "tc-2", Name = "read2", ArgumentsJson = "{}" });
        executor.AddTool(executionState, new ToolCall { Id = "tc-3", Name = "write1", ArgumentsJson = "{}" });

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

        var executor = new StreamingToolExecutor(tools, toolMiddlewares: [middleware]);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-fail", Name = "failing", ArgumentsJson = "{}" });
        executor.AddTool(executionState, new ToolCall { Id = "tc-skip", Name = "skipped", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
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

        var executor = new StreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-1", Name = "slow", ArgumentsJson = "{}" });
        executor.AddTool(executionState, new ToolCall { Id = "tc-2", Name = "queued", ArgumentsJson = "{}" });

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

        var executor = new StreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();
        executor.Discard(executionState);

        executor.AddTool(executionState, new ToolCall { Id = "tc-late", Name = "echo", ArgumentsJson = "{}" });

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

        var executor = new StreamingToolExecutor(tools);
        using var firstState = executor.CreateExecutionState();
        using var secondState = executor.CreateExecutionState();

        executor.AddTool(firstState, new ToolCall { Id = "first", Name = "blocked", ArgumentsJson = "{}" });
        executor.AddTool(secondState, new ToolCall { Id = "second", Name = "echo", ArgumentsJson = "{}" });
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

        var executor = new StreamingToolExecutor(
            tools, toolContext: toolContext);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-1", Name = "meta-check", ArgumentsJson = "{}" });

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

        var executor = new StreamingToolExecutor(
            tools,
            requestMetadata: metadata);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-1", Name = "meta-check", ArgumentsJson = "{}" });

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
        var executor = new StreamingToolExecutor(
            tools,
            requestMetadata: requestMetadata,
            toolContext: typedContext);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-typed", Name = "meta-check", ArgumentsJson = "{}" });

        await foreach (var _ in executor.GetRemainingResultsAsync(executionState, CancellationToken.None)) { }

        capturedToken.Should().Be("typed-token");
        capturedExternal.Should().Be("typed-trace");
        capturedCallId.Should().Be("tc-typed");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task NullRequestMetadataAndContext_ShouldNotCreateImplicitToolExecutionContext()
    {
        AgentToolExecutionContext? capturedContext = AgentToolExecutionContext.Empty;
        var tools = new ToolManager();
        tools.Register(new DelegateAgentTool("meta-check", _ =>
        {
            capturedContext = AgentToolRequestContext.Current;
            return "ok";
        }));

        var executor = new StreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-1", Name = "meta-check", ArgumentsJson = "{}" });

        await foreach (var _ in executor.GetRemainingResultsAsync(executionState, CancellationToken.None)) { }

        capturedContext.Should().BeNull();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task GetCompletedResults_ShouldReturnNonBlocking()
    {
        var tools = new ToolManager();
        tools.Register(new ConcurrencyTrackingTool("echo", isReadOnly: true, _ => "ok"));

        var executor = new StreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        // Before adding any tools
        executor.GetCompletedResults(executionState).Should().BeEmpty();

        executor.AddTool(executionState, new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" });

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

        var executor = new StreamingToolExecutor(tools, hooks, [middleware]);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" });
        executor.AddTool(executionState, new ToolCall { Id = "tc-2", Name = "echo", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(2);
        hook.ToolStartCount.Should().Be(2);
        hook.ToolEndCount.Should().Be(2);
        middlewareCalls.Should().Be(2);
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
        var executor = new StreamingToolExecutor(tools, hooks);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-rewrite", Name = "read", ArgumentsJson = "{}" });
        executor.AddTool(executionState, new ToolCall { Id = "tc-queued", Name = "queued", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(2);
        results[0].CallId.Should().Be("tc-rewrite");
        results[0].IsError.Should().BeTrue();
        results[0].Result.Should().Contain("rewrote a concurrent read-only call to a non-read-only tool");
        results[1].CallId.Should().Be("tc-queued");
        results[1].IsError.Should().BeTrue();
        results[1].Result.Should().Contain("prior tool error");
        destructiveRan.Should().BeFalse("rewritten non-read-only tool must not execute after concurrent admission");
        skippedRan.Should().BeFalse("scheduler fault should prevent queued tools from executing");
    }

    [Fact]
    public async Task UnknownTool_ShouldReturnNotFoundResult()
    {
        var tools = new ToolManager();
        var executor = new StreamingToolExecutor(tools);
        using var executionState = executor.CreateExecutionState();

        executor.AddTool(executionState, new ToolCall { Id = "tc-1", Name = "nonexistent", ArgumentsJson = "{}" });

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(executionState, CancellationToken.None))
            results.Add(result);

        results.Should().HaveCount(1);
        results[0].CallId.Should().Be("tc-1");
        results[0].Result.Should().Contain("not found");
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
