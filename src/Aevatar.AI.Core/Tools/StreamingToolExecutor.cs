// ─────────────────────────────────────────────────────────────
// StreamingToolExecutor — 流式并发工具执行器
// 边解析边执行：LLM 流式返回的 tool_use block 一完整就立即调度。
// ReadOnly 工具并行执行，写操作串行排队。
// 结果按调用顺序 yield，保持对话流一致性。
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aevatar.AI.Core.Tools;

/// <summary>Tool execution result with call-id for message pairing.</summary>
public readonly record struct ToolExecutionResult(string CallId, string Result, bool IsError);

/// <summary>
/// Streaming tool executor that starts executing tools as soon as they appear,
/// runs read-only tools in parallel, and yields results in call-order.
/// </summary>
// Refactor (iter1/cluster-005):
//   Old pattern: lock-protected scheduler state was mutated by caller and background tool tasks.
//   New principle: tool execution state advances only through one serialized channel coordinator loop.
public sealed class StreamingToolExecutor : IDisposable
{
    private readonly ToolManager _tools;
    private readonly AgentHookPipeline? _hooks;
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;
    private readonly AgentToolExecutionContext? _toolContext;
    private readonly Channel<CoordinatorSignal> _signals = Channel.CreateUnbounded<CoordinatorSignal>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<ToolExecutionResult> _readyResults = Channel.CreateUnbounded<ToolExecutionResult>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly CancellationTokenSource _discardCts = new();

    public StreamingToolExecutor(
        ToolManager tools,
        AgentHookPipeline? hooks = null,
        IReadOnlyList<IToolCallMiddleware>? toolMiddlewares = null,
        IReadOnlyDictionary<string, string>? requestMetadata = null,
        AgentToolExecutionContext? toolContext = null)
    {
        // Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
        //   Old pattern: streaming tool execution received raw request Metadata.
        //   New principle: tool control semantics are typed context fields; Metadata is not the internal control plane.
        _tools = tools;
        _hooks = hooks;
        _toolMiddlewares = toolMiddlewares ?? [];
        _toolContext = toolContext
            ?? AgentToolRequestContext.Current
            ?? AgentToolExecutionContextMapper.FromMetadata(requestMetadata);
        _ = RunCoordinatorAsync();
    }

    /// <summary>
    /// Queue a tool for execution. Immediately starts if concurrency rules allow.
    /// If <see cref="Discard"/> has already been called, the tool is recorded as
    /// an immediate discard-error without scheduling.
    /// </summary>
    public void AddTool(ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        PostSignal(new ToolDiscoveredSignal(toolCall));
    }

    /// <summary>
    /// Non-blocking: returns completed results in call-order.
    /// Stops at the first non-completed tool to preserve ordering.
    /// </summary>
    public List<ToolExecutionResult> GetCompletedResults()
    {
        var results = new List<ToolExecutionResult>();
        while (_readyResults.Reader.TryRead(out var result))
            results.Add(result);
        return results;
    }

    /// <summary>
    /// Async: waits for all in-progress tools and yields results in call-order.
    /// </summary>
    public async IAsyncEnumerable<ToolExecutionResult> GetRemainingResultsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            foreach (var result in GetCompletedResults())
                yield return result;

            var snapshot = await RequestStateAsync(ct);
            var lateResults = GetCompletedResults();
            if (lateResults.Count > 0)
            {
                foreach (var result in lateResults)
                    yield return result;
                continue;
            }

            if (!snapshot.HasRemainingTools)
                yield break;

            await snapshot.NextChange.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Cancel all queued tools immediately. Executing tools are cancelled via the
    /// token but allowed to complete naturally — their <see cref="TrackedTool.Completion"/>
    /// will be set when <see cref="ExecuteToolAsync"/> exits.
    /// </summary>
    public void Discard()
    {
        _discardCts.Cancel();
        PostSignal(DiscardSignal.Instance);
    }

    public void Dispose()
    {
        Discard();
        _signals.Writer.TryComplete();
        _discardCts.Dispose();
    }

    // ─── Internal execution logic ───

    private async Task RunCoordinatorAsync()
    {
        var state = new CoordinatorState();
        await foreach (var signal in _signals.Reader.ReadAllAsync())
        {
            switch (signal)
            {
                case ToolDiscoveredSignal discovered:
                    HandleToolDiscovered(state, discovered.ToolCall);
                    break;
                case ToolCompletedSignal completed:
                    HandleToolCompleted(state, completed);
                    break;
                case SchedulerFaultSignal:
                    state.HasErrored = true;
                    break;
                case DiscardSignal:
                    state.Discarded = true;
                    CompletePendingToolsAsDiscarded(state);
                    break;
                case StateRequestSignal request:
                    request.Completion.TrySetResult(CreateSnapshot(state));
                    continue;
            }

            ProcessQueue(state);
            PublishAvailableResults(state);
            NotifyWaiters(state);
        }
    }

    private void HandleToolDiscovered(CoordinatorState state, ToolCall toolCall)
    {
        var tool = _tools.Get(toolCall.Name);
        var tracked = new TrackedTool(
            Index: state.Tools.Count,
            Call: toolCall,
            Tool: tool,
            IsConcurrencySafe: tool?.IsReadOnly == true && tool.IsDestructive == false);

        state.Tools.Add(tracked);

        if (state.Discarded)
        {
            tracked.Status = ToolStatus.Completed;
            tracked.Result = new ToolExecutionResult(
                toolCall.Id,
                "Tool execution was discarded",
                IsError: true);
        }
    }

    private static void HandleToolCompleted(CoordinatorState state, ToolCompletedSignal completed)
    {
        if (completed.Index < 0 || completed.Index >= state.Tools.Count)
            return;

        var tracked = state.Tools[completed.Index];
        if (tracked.Status != ToolStatus.Executing)
            return;

        tracked.Status = ToolStatus.Completed;
        tracked.Result = completed.Result;
        if (completed.Result.IsError)
            state.HasErrored = true;
    }

    private void ProcessQueue(CoordinatorState state)
    {
        foreach (var tracked in state.Tools)
        {
            if (tracked.Status != ToolStatus.Queued)
                continue;

            if (state.HasErrored || state.Discarded)
            {
                tracked.Status = ToolStatus.Completed;
                tracked.Result = new ToolExecutionResult(
                    tracked.Call.Id,
                    state.Discarded ? "Tool execution was discarded" : "Skipped due to prior tool error",
                    IsError: true);
                continue;
            }

            if (CanExecute(state, tracked.IsConcurrencySafe))
            {
                tracked.Status = ToolStatus.Executing;
                _ = ExecuteToolAsync(tracked);
            }
            else if (!tracked.IsConcurrencySafe)
            {
                break;
            }
        }
    }

    private static bool CanExecute(CoordinatorState state, bool isConcurrencySafe)
    {
        var executing = state.Tools.Where(static tracked => tracked.Status == ToolStatus.Executing).ToList();
        if (executing.Count == 0)
            return true;

        return isConcurrencySafe && executing.All(static tracked => tracked.IsConcurrencySafe);
    }

    private void PublishAvailableResults(CoordinatorState state)
    {
        while (state.NextResultIndex < state.Tools.Count)
        {
            var tracked = state.Tools[state.NextResultIndex];
            if (tracked.Status != ToolStatus.Completed || tracked.Result is not { } result)
                break;

            tracked.Status = ToolStatus.Yielded;
            state.NextResultIndex++;
            _readyResults.Writer.TryWrite(result);
            state.PublishedResult = true;
        }
    }

    private static void CompletePendingToolsAsDiscarded(CoordinatorState state)
    {
        foreach (var tracked in state.Tools)
        {
            if (tracked.Status is ToolStatus.Completed or ToolStatus.Yielded)
                continue;

            tracked.Status = ToolStatus.Completed;
            tracked.Result = new ToolExecutionResult(
                tracked.Call.Id,
                "Tool execution was discarded",
                IsError: true);
        }
    }

    private ExecutorStateSnapshot CreateSnapshot(CoordinatorState state)
    {
        if (!HasRemainingTools(state))
            return new ExecutorStateSnapshot(false, Task.CompletedTask);

        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Waiters.Add(waiter);
        return new ExecutorStateSnapshot(true, waiter.Task);
    }

    private static bool HasRemainingTools(CoordinatorState state) =>
        state.Tools.Any(static tracked => tracked.Status != ToolStatus.Yielded);

    private static void NotifyWaiters(CoordinatorState state)
    {
        if (!state.PublishedResult && HasRemainingTools(state))
            return;

        foreach (var waiter in state.Waiters)
            waiter.TrySetResult();
        state.Waiters.Clear();
        state.PublishedResult = false;
    }

    private async Task<ExecutorStateSnapshot> RequestStateAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<ExecutorStateSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _signals.Writer.WriteAsync(new StateRequestSignal(completion), ct);
        return await completion.Task.WaitAsync(ct);
    }

    private void PostSignal(CoordinatorSignal signal) => _signals.Writer.TryWrite(signal);

    private async Task ExecuteToolAsync(TrackedTool tracked)
    {
        try
        {
            using var _ = AgentToolContextScope.Push(_toolContext?.WithCallId(tracked.Call.Id));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_discardCts.Token);
            var ct = linked.Token;

            var call = tracked.Call;
            var toolCtx = new AIGAgentExecutionHookContext
            {
                ToolName = call.Name,
                ToolArguments = call.ArgumentsJson,
                ToolCallId = call.Id,
            };
            try { if (_hooks != null) await _hooks.RunToolExecuteStartAsync(toolCtx, ct); }
            catch { /* Hook failures must not crash tool execution */ }

            // Re-resolve tool after hooks — hooks may have rewritten the tool name.
            var effectiveToolName = string.IsNullOrWhiteSpace(toolCtx.ToolName) ? call.Name : toolCtx.ToolName!;
            var effectiveTool = _tools.Get(effectiveToolName) ?? tracked.Tool ?? new NullAgentTool(call.Name);

            // If the hook changed the tool name to a different tool, re-evaluate concurrency
            // conservatively: force serial if the resolved tool is not read-only.
            if (!string.Equals(effectiveToolName, call.Name, StringComparison.OrdinalIgnoreCase))
            {
                var resolvedIsConcurrencySafe = effectiveTool.IsReadOnly && !effectiveTool.IsDestructive;
                if (!resolvedIsConcurrencySafe && tracked.IsConcurrencySafe)
                {
                    // The tool was admitted as concurrent but the rewritten tool is not —
                    // we cannot retroactively serialize, but we record this as an error
                    // to prevent further damage in follow-up rounds.
                    PostSignal(new SchedulerFaultSignal(tracked.Index));
                }
            }

            var toolCallContext = new ToolCallContext
            {
                Tool = effectiveTool,
                ToolName = effectiveToolName,
                ToolCallId = call.Id,
                ArgumentsJson = toolCtx.ToolArguments ?? call.ArgumentsJson,
                CancellationToken = ct,
            };

            await MiddlewarePipeline.RunToolCallAsync(_toolMiddlewares, toolCallContext, async () =>
            {
                if (toolCallContext.Terminate) return;

                var resolvedCall = new ToolCall
                {
                    Id = toolCallContext.ToolCallId,
                    Name = toolCallContext.ToolName,
                    ArgumentsJson = toolCallContext.ArgumentsJson,
                };

                var result = await _tools.ExecuteToolCallAsync(resolvedCall, ct);
                toolCallContext.Result = result.Content;
            });

            var toolResult = toolCallContext.Result
                ?? (toolCallContext.Terminate
                    ? "Tool call terminated by middleware"
                    : $"Tool '{toolCallContext.ToolName}' returned no result");

            toolCtx.ToolResult = toolResult;
            try { if (_hooks != null) await _hooks.RunToolExecuteEndAsync(toolCtx, ct); }
            catch { /* Hook failures must not crash tool execution */ }

            if (_discardCts.IsCancellationRequested)
            {
                PostSignal(new ToolCompletedSignal(
                    tracked.Index,
                    new ToolExecutionResult(
                        call.Id, "Tool execution was discarded", IsError: true)));
                return;
            }

            PostSignal(new ToolCompletedSignal(
                tracked.Index,
                new ToolExecutionResult(call.Id, toolResult, IsError: false)));
        }
        catch (OperationCanceledException) when (_discardCts.IsCancellationRequested)
        {
            PostSignal(new ToolCompletedSignal(
                tracked.Index,
                new ToolExecutionResult(
                    tracked.Call.Id, "Tool execution was discarded", IsError: true)));
        }
        catch (Exception ex)
        {
            PostSignal(new ToolCompletedSignal(
                tracked.Index,
                new ToolExecutionResult(
                    tracked.Call.Id, ToolManager.BuildErrorJson(ex.Message), IsError: true)));
        }
        finally
        {
        }
    }

    private sealed class NullAgentTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => "";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult($"Tool '{name}' not found");
    }

    private enum ToolStatus { Queued, Executing, Completed, Yielded }

    private sealed class TrackedTool(
        int Index,
        ToolCall Call,
        IAgentTool? Tool,
        bool IsConcurrencySafe)
    {
        public int Index { get; } = Index;
        public ToolCall Call { get; } = Call;
        public IAgentTool? Tool { get; } = Tool;
        public bool IsConcurrencySafe { get; } = IsConcurrencySafe;
        public ToolStatus Status { get; set; }
        public ToolExecutionResult? Result { get; set; }
    }

    private sealed class CoordinatorState
    {
        public List<TrackedTool> Tools { get; } = [];
        public List<TaskCompletionSource> Waiters { get; } = [];
        public int NextResultIndex { get; set; }
        public bool HasErrored { get; set; }
        public bool Discarded { get; set; }
        public bool PublishedResult { get; set; }
    }

    private readonly record struct ExecutorStateSnapshot(bool HasRemainingTools, Task NextChange);

    private abstract record CoordinatorSignal;
    private sealed record ToolDiscoveredSignal(ToolCall ToolCall) : CoordinatorSignal;
    private sealed record ToolCompletedSignal(int Index, ToolExecutionResult Result) : CoordinatorSignal;
    private sealed record SchedulerFaultSignal(int Index) : CoordinatorSignal;
    private sealed record StateRequestSignal(TaskCompletionSource<ExecutorStateSnapshot> Completion) : CoordinatorSignal;
    private sealed record DiscardSignal : CoordinatorSignal
    {
        public static DiscardSignal Instance { get; } = new();
    }
}
