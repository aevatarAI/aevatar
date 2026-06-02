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
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Aevatar.AI.Core.Tools;

/// <summary>Tool execution result with call-id for message pairing.</summary>
public readonly record struct ToolExecutionResult(string CallId, string Result, bool IsError);

/// <summary>
/// Streaming tool executor that starts executing tools as soon as they appear,
/// runs read-only tools in parallel, and yields results in call-order.
/// </summary>
// Refactor (iter35/cluster-040-streaming-tool-executor):
//   Old pattern: StreamingToolExecutor owns process-local channel coordinator + TaskCompletionSource waiters + List<TrackedTool>/List<TaskCompletionSource> as object fields for tool execution ordering.
//   New principle: Tool execution state kept in owning chat/actor turn,或 narrow runtime-neutral tool scheduling abstraction(no process-local progress storage)。Streaming tool progress advanced by owning execution flow;process-local channels 仅作 transport mechanics,不作 business progress 来源。
public sealed class StreamingToolExecutor
{
    private readonly ToolManager _tools;
    private readonly AgentHookPipeline? _hooks;
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;
    private readonly AgentToolExecutionContext? _toolContext;

    public StreamingToolExecutor(
        ToolManager tools,
        AgentHookPipeline? hooks = null,
        IReadOnlyList<IToolCallMiddleware>? toolMiddlewares = null,
        IReadOnlyDictionary<string, string>? requestMetadata = null,
        AgentToolExecutionContext? toolContext = null)
    {
        // Refactor (issue1574): Old pattern: streaming tool execution promoted request Metadata into tool control.
        // New principle: streaming tool control is typed; request Metadata remains external annotations only.
        _tools = tools;
        _hooks = hooks;
        _toolMiddlewares = toolMiddlewares ?? [];
        _toolContext = toolContext
            ?? AgentToolRequestContext.Current
            ?? (requestMetadata == null
                ? null
                : AgentToolExecutionContext.Empty with
                {
                    ExternalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(requestMetadata),
                });
    }

    public ExecutionState CreateExecutionState() => new();

    /// <summary>
    /// Queue a tool for execution. Immediately starts if concurrency rules allow.
    /// If <see cref="Discard"/> has already been called, the tool is recorded as
    /// an immediate discard-error without scheduling.
    /// </summary>
    public void AddTool(ExecutionState state, ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(toolCall);

        var tool = _tools.Get(toolCall.Name);
        var tracked = new ToolExecutionEntry(
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

        Advance(state);
    }

    /// <summary>
    /// Non-blocking: returns completed results in call-order.
    /// Stops at the first non-completed tool to preserve ordering.
    /// </summary>
    public List<ToolExecutionResult> GetCompletedResults(ExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        CompleteFinishedTools(state);
        Advance(state);
        return DrainReadyResults(state);
    }

    /// <summary>
    /// Async: waits for all in-progress tools and yields results in call-order.
    /// </summary>
    public async IAsyncEnumerable<ToolExecutionResult> GetRemainingResultsAsync(
        ExecutionState state,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        while (true)
        {
            foreach (var result in GetCompletedResults(state))
                yield return result;

            if (!HasRemainingTools(state))
                yield break;

            var completions = state.Tools
                .Where(static tracked => tracked.Status == ToolStatus.Executing)
                .Select(static tracked => tracked.Execution!)
                .ToArray();
            if (completions.Length == 0)
            {
                Advance(state);
                continue;
            }

            await Task.WhenAny(completions).WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancel all queued tools immediately. Executing tools are cancelled via the
    /// token but allowed to complete naturally.
    /// </summary>
    public void Discard(ExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.Discarded = true;
        state.DiscardCts.Cancel();
        CompletePendingToolsAsDiscarded(state);
        Advance(state);
    }

    private void Advance(ExecutionState state)
    {
        CompleteFinishedTools(state);
        ProcessQueue(state);
        PublishAvailableResults(state);
    }

    private static void CompleteFinishedTools(ExecutionState state)
    {
        foreach (var tracked in state.Tools)
        {
            if (tracked.Status != ToolStatus.Executing || tracked.Execution is not { IsCompleted: true } execution)
                continue;

            ToolExecutionCompletion completion;
            if (execution.IsCanceled && state.DiscardCts.IsCancellationRequested)
            {
                completion = new ToolExecutionCompletion(
                    new ToolExecutionResult(
                        tracked.Call.Id,
                        "Tool execution was discarded",
                        IsError: true),
                    SchedulerFault: false);
            }
            else if (execution.IsFaulted)
            {
                var ex = execution.Exception.GetBaseException();
                completion = new ToolExecutionCompletion(
                    new ToolExecutionResult(
                        tracked.Call.Id,
                        ToolManager.BuildErrorJson(ex.Message),
                        IsError: true),
                    SchedulerFault: false);
            }
            else
            {
                completion = execution.Result;
            }

            tracked.Status = ToolStatus.Completed;
            tracked.Result = completion.Result;
            if (completion.Result.IsError || completion.SchedulerFault)
                state.HasErrored = true;
        }
    }

    private void ProcessQueue(ExecutionState state)
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
                tracked.Execution = ExecuteToolAsync(state.DiscardCts.Token, tracked);
            }
            else if (!tracked.IsConcurrencySafe)
            {
                break;
            }
        }
    }

    private static bool CanExecute(ExecutionState state, bool isConcurrencySafe)
    {
        var executing = state.Tools.Where(static tracked => tracked.Status == ToolStatus.Executing).ToList();
        if (executing.Count == 0)
            return true;

        return isConcurrencySafe && executing.All(static tracked => tracked.IsConcurrencySafe);
    }

    private static void PublishAvailableResults(ExecutionState state)
    {
        while (state.NextResultIndex < state.Tools.Count)
        {
            var tracked = state.Tools[state.NextResultIndex];
            if (tracked.Status != ToolStatus.Completed || tracked.Result is not { } result)
                break;

            tracked.Status = ToolStatus.Yielded;
            state.NextResultIndex++;
            state.ReadyResults.Add(result);
        }
    }

    private static List<ToolExecutionResult> DrainReadyResults(ExecutionState state)
    {
        if (state.ReadyResults.Count == 0)
            return [];

        var results = state.ReadyResults;
        state.ReadyResults = [];
        return results;
    }

    private static void CompletePendingToolsAsDiscarded(ExecutionState state)
    {
        foreach (var tracked in state.Tools)
        {
            if (tracked.Status is ToolStatus.Completed or ToolStatus.Yielded)
                continue;

            if (tracked.Status == ToolStatus.Executing && tracked.Execution is { IsCompleted: false })
                continue;

            tracked.Status = ToolStatus.Completed;
            tracked.Result = new ToolExecutionResult(
                tracked.Call.Id,
                "Tool execution was discarded",
                IsError: true);
        }
    }

    private static bool HasRemainingTools(ExecutionState state) =>
        state.Tools.Any(static tracked => tracked.Status != ToolStatus.Yielded);

    private async Task<ToolExecutionCompletion> ExecuteToolAsync(CancellationToken ct, ToolExecutionEntry tracked)
    {
        try
        {
            using var _ = AgentToolContextScope.Push(_toolContext?.WithCallId(tracked.Call.Id));

            var call = tracked.Call;
            var toolCtx = new AIGAgentExecutionHookContext
            {
                ToolName = call.Name,
                ToolArguments = call.ArgumentsJson,
                ToolCallId = call.Id,
            };
            try { if (_hooks != null) await _hooks.RunToolExecuteStartAsync(toolCtx, ct); }
            catch { /* Hook failures must not crash tool execution */ }
            var toolStartedAt = Stopwatch.GetTimestamp();

            // Re-resolve tool after hooks — hooks may have rewritten the tool name.
            var effectiveToolName = string.IsNullOrWhiteSpace(toolCtx.ToolName) ? call.Name : toolCtx.ToolName!;
            var effectiveTool = _tools.Get(effectiveToolName) ?? tracked.Tool ?? new NullAgentTool(call.Name);

            // If the hook changed the tool name to a different tool, re-evaluate concurrency
            // conservatively: force serial if the resolved tool is not read-only.
            if (!string.Equals(effectiveToolName, call.Name, StringComparison.OrdinalIgnoreCase))
            {
                var resolvedIsConcurrencySafe = effectiveTool.IsReadOnly && !effectiveTool.IsDestructive;
                if (!resolvedIsConcurrencySafe && tracked.IsConcurrencySafe)
                    return new ToolExecutionCompletion(
                        new ToolExecutionResult(
                            call.Id,
                            ToolManager.BuildErrorJson("Tool hook rewrote a concurrent read-only call to a non-read-only tool."),
                            IsError: true),
                        SchedulerFault: true);
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
            toolCtx.Duration = Stopwatch.GetElapsedTime(toolStartedAt);
            try { if (_hooks != null) await _hooks.RunToolExecuteEndAsync(toolCtx, ct); }
            catch { /* Hook failures must not crash tool execution */ }

            if (ct.IsCancellationRequested)
                return new ToolExecutionCompletion(
                    new ToolExecutionResult(call.Id, "Tool execution was discarded", IsError: true),
                    SchedulerFault: false);

            return new ToolExecutionCompletion(
                new ToolExecutionResult(call.Id, toolResult, IsError: false),
                SchedulerFault: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ToolExecutionCompletion(
                new ToolExecutionResult(
                    tracked.Call.Id,
                    "Tool execution was discarded",
                    IsError: true),
                SchedulerFault: false);
        }
        catch (Exception ex)
        {
            return new ToolExecutionCompletion(
                new ToolExecutionResult(
                    tracked.Call.Id,
                    ToolManager.BuildErrorJson(ex.Message),
                    IsError: true),
                SchedulerFault: false);
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

    internal enum ToolStatus { Queued, Executing, Completed, Yielded }

    internal sealed class ToolExecutionEntry(
        ToolCall Call,
        IAgentTool? Tool,
        bool IsConcurrencySafe)
    {
        public ToolCall Call { get; } = Call;
        public IAgentTool? Tool { get; } = Tool;
        public bool IsConcurrencySafe { get; } = IsConcurrencySafe;
        public ToolStatus Status { get; set; }
        public ToolExecutionResult? Result { get; set; }
        public Task<ToolExecutionCompletion>? Execution { get; set; }
    }

    internal readonly record struct ToolExecutionCompletion(ToolExecutionResult Result, bool SchedulerFault);

    // Refactor (iter35/cluster-040-streaming-tool-executor):
    //   Old pattern: StreamingToolExecutor owns process-local channel coordinator + TaskCompletionSource waiters + List<TrackedTool>/List<TaskCompletionSource> as object fields for tool execution ordering.
    //   New principle: Tool execution state kept in owning chat/actor turn,或 narrow runtime-neutral tool scheduling abstraction(no process-local progress storage)。Streaming tool progress advanced by owning execution flow;process-local channels 仅作 transport mechanics,不作 business progress 来源。
    // refactor helper, no behavior change: per-turn scheduling state explicitly owned by the chat/tool execution flow.
    public sealed class ExecutionState : IDisposable
    {
        internal List<ToolExecutionEntry> Tools { get; } = [];
        internal List<ToolExecutionResult> ReadyResults { get; set; } = [];
        internal CancellationTokenSource DiscardCts { get; } = new();
        internal int NextResultIndex { get; set; }
        internal bool HasErrored { get; set; }
        internal bool Discarded { get; set; }

        public void Dispose()
        {
            DiscardCts.Cancel();
            DiscardCts.Dispose();
        }
    }
}
