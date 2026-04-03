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

namespace Aevatar.AI.Core.Tools;

/// <summary>Tool execution result with call-id for message pairing.</summary>
public readonly record struct ToolExecutionResult(string CallId, string Result, bool IsError);

/// <summary>
/// Streaming tool executor that starts executing tools as soon as they appear,
/// runs read-only tools in parallel, and yields results in call-order.
/// </summary>
public sealed class StreamingToolExecutor : IDisposable
{
    private readonly ToolManager _tools;
    private readonly AgentHookPipeline? _hooks;
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;
    private readonly IReadOnlyDictionary<string, string>? _requestMetadata;
    private readonly List<TrackedTool> _trackedTools = [];
    private readonly CancellationTokenSource _discardCts = new();
    private readonly object _lock = new();
    private bool _hasErrored;
    private bool _discarded;

    public StreamingToolExecutor(
        ToolManager tools,
        AgentHookPipeline? hooks = null,
        IReadOnlyList<IToolCallMiddleware>? toolMiddlewares = null,
        IReadOnlyDictionary<string, string>? requestMetadata = null)
    {
        _tools = tools;
        _hooks = hooks;
        _toolMiddlewares = toolMiddlewares ?? [];
        _requestMetadata = requestMetadata;
    }

    /// <summary>
    /// Queue a tool for execution. Immediately starts if concurrency rules allow.
    /// If <see cref="Discard"/> has already been called, the tool is recorded as
    /// an immediate discard-error without scheduling.
    /// </summary>
    public void AddTool(ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        var tool = _tools.Get(toolCall.Name);
        var isConcurrencySafe = tool?.IsReadOnly == true && tool.IsDestructive == false;

        lock (_lock)
        {
            if (_discarded)
            {
                var discardedTool = new TrackedTool
                {
                    Call = toolCall,
                    Tool = tool,
                    IsConcurrencySafe = isConcurrencySafe,
                    Status = ToolStatus.Completed,
                    Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                };
                discardedTool.Result = new ToolExecutionResult(
                    toolCall.Id, "Tool execution was discarded", IsError: true);
                discardedTool.Completion.TrySetResult();
                _trackedTools.Add(discardedTool);
                return;
            }

            _trackedTools.Add(new TrackedTool
            {
                Call = toolCall,
                Tool = tool,
                IsConcurrencySafe = isConcurrencySafe,
                Status = ToolStatus.Queued,
                Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            });
        }

        _ = ProcessQueueAsync();
    }

    /// <summary>
    /// Non-blocking: returns completed results in call-order.
    /// Stops at the first non-completed tool to preserve ordering.
    /// </summary>
    public List<ToolExecutionResult> GetCompletedResults()
    {
        var results = new List<ToolExecutionResult>();
        lock (_lock)
        {
            foreach (var tracked in _trackedTools)
            {
                if (tracked.Status == ToolStatus.Yielded)
                    continue;

                if (tracked.Status == ToolStatus.Completed)
                {
                    tracked.Status = ToolStatus.Yielded;
                    results.Add(tracked.Result!.Value);
                }
                else
                {
                    // Must preserve call-order: stop at first non-completed
                    break;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Async: waits for all in-progress tools and yields results in call-order.
    /// </summary>
    public async IAsyncEnumerable<ToolExecutionResult> GetRemainingResultsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            TrackedTool? waitFor = null;

            // Yield any completed results in order
            foreach (var result in GetCompletedResults())
                yield return result;

            lock (_lock)
            {
                // Find the first non-yielded tool
                foreach (var tracked in _trackedTools)
                {
                    if (tracked.Status == ToolStatus.Yielded)
                        continue;

                    if (tracked.Status == ToolStatus.Completed)
                    {
                        // Will be picked up by next GetCompletedResults() call
                        break;
                    }

                    // Executing or Queued — wait for it
                    waitFor = tracked;
                    break;
                }

                if (waitFor == null)
                    yield break; // All done
            }

            // Wait for the next tool to complete
            var discarded = false;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _discardCts.Token))
            {
                try
                {
                    await waitFor.Completion.Task.WaitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (_discardCts.IsCancellationRequested)
                {
                    discarded = true;
                }
            }

            if (discarded)
            {
                // Discard was called — yield synthetic errors for remaining tools
                foreach (var result in DrainAsErrors("Execution discarded"))
                    yield return result;
                yield break;
            }

            // Kick the queue in case new tools became eligible
            await ProcessQueueAsync();
        }
    }

    /// <summary>
    /// Cancel all queued tools immediately. Executing tools are cancelled via the
    /// token but allowed to complete naturally — their <see cref="TrackedTool.Completion"/>
    /// will be set when <see cref="ExecuteToolAsync"/> exits.
    /// </summary>
    public void Discard()
    {
        lock (_lock)
        {
            _discarded = true;
        }

        _discardCts.Cancel();

        lock (_lock)
        {
            foreach (var tracked in _trackedTools)
            {
                // Only force-complete queued tools; executing tools will observe
                // the cancellation token and complete themselves.
                if (tracked.Status == ToolStatus.Queued)
                {
                    tracked.Status = ToolStatus.Completed;
                    tracked.Result = new ToolExecutionResult(
                        tracked.Call.Id,
                        "Tool execution was discarded",
                        IsError: true);
                    tracked.Completion.TrySetResult();
                }
            }
        }
    }

    public void Dispose() => _discardCts.Dispose();

    // ─── Internal execution logic ───

    private async Task ProcessQueueAsync()
    {
        List<TrackedTool> toExecute = [];

        lock (_lock)
        {
            foreach (var tracked in _trackedTools)
            {
                if (tracked.Status != ToolStatus.Queued)
                    continue;

                if (_hasErrored || _discarded)
                {
                    // Error cascade or discard: give synthetic error to queued tools
                    tracked.Status = ToolStatus.Completed;
                    tracked.Result = new ToolExecutionResult(
                        tracked.Call.Id,
                        _discarded ? "Tool execution was discarded" : "Skipped due to prior tool error",
                        IsError: true);
                    tracked.Completion.TrySetResult();
                    continue;
                }

                if (CanExecute(tracked.IsConcurrencySafe))
                {
                    tracked.Status = ToolStatus.Executing;
                    toExecute.Add(tracked);
                }
                else if (!tracked.IsConcurrencySafe)
                {
                    // Non-concurrent tool must wait; stop scanning
                    break;
                }
            }
        }

        foreach (var tracked in toExecute)
            _ = ExecuteToolAsync(tracked);
    }

    private bool CanExecute(bool isConcurrencySafe)
    {
        // Must hold _lock
        var hasExecuting = false;
        var allExecutingAreConcurrencySafe = true;

        foreach (var tracked in _trackedTools)
        {
            if (tracked.Status != ToolStatus.Executing)
                continue;

            hasExecuting = true;
            if (!tracked.IsConcurrencySafe)
            {
                allExecutingAreConcurrencySafe = false;
                break;
            }
        }

        if (!hasExecuting)
            return true;

        // Both must be concurrency-safe
        return isConcurrencySafe && allExecutingAreConcurrencySafe;
    }

    private async Task ExecuteToolAsync(TrackedTool tracked)
    {
        try
        {
            // Propagate request metadata (e.g. NyxID access token) to the tool's AsyncLocal context.
            AgentToolRequestContext.CurrentMetadata = _requestMetadata;

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
                    lock (_lock)
                    {
                        _hasErrored = true;
                    }
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

            lock (_lock)
            {
                // Guard against late write after Discard()
                if (tracked.Status != ToolStatus.Executing) return;
                tracked.Status = ToolStatus.Completed;
                tracked.Result = new ToolExecutionResult(call.Id, toolResult, IsError: false);
            }
        }
        catch (OperationCanceledException) when (_discardCts.IsCancellationRequested)
        {
            lock (_lock)
            {
                if (tracked.Status != ToolStatus.Executing) return;
                tracked.Status = ToolStatus.Completed;
                tracked.Result = new ToolExecutionResult(
                    tracked.Call.Id, "Tool execution was discarded", IsError: true);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (tracked.Status != ToolStatus.Executing) return;
                _hasErrored = true;
                tracked.Status = ToolStatus.Completed;
                tracked.Result = new ToolExecutionResult(
                    tracked.Call.Id, ToolManager.BuildErrorJson(ex.Message), IsError: true);
            }
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
            tracked.Completion.TrySetResult();
            // Process queue: a non-concurrent tool may have been waiting
            _ = ProcessQueueAsync();
        }
    }

    private List<ToolExecutionResult> DrainAsErrors(string reason)
    {
        var results = new List<ToolExecutionResult>();
        lock (_lock)
        {
            foreach (var tracked in _trackedTools)
            {
                if (tracked.Status == ToolStatus.Yielded)
                    continue;

                if (tracked.Status == ToolStatus.Completed)
                {
                    tracked.Status = ToolStatus.Yielded;
                    results.Add(tracked.Result!.Value);
                    continue;
                }

                tracked.Status = ToolStatus.Yielded;
                results.Add(new ToolExecutionResult(tracked.Call.Id, reason, IsError: true));
            }
        }

        return results;
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

    private sealed class TrackedTool
    {
        public required ToolCall Call { get; init; }
        public IAgentTool? Tool { get; init; }
        public required bool IsConcurrencySafe { get; init; }
        public ToolStatus Status { get; set; }
        public ToolExecutionResult? Result { get; set; }
        public required TaskCompletionSource Completion { get; init; }
    }
}
