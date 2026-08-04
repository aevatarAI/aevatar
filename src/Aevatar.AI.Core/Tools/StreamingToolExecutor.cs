// ─────────────────────────────────────────────────────────────
// StreamingToolExecutor — 流式并发工具执行器
// 边解析边执行：LLM 流式返回的 tool_use block 一完整就立即调度。
// ReadOnly 工具并行执行，写操作串行排队。
// 结果按调用顺序 yield，保持对话流一致性。
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Aevatar.AI.Core.Tools;

/// <summary>Tool execution result with call-id for message pairing.</summary>
public readonly record struct ToolExecutionResult(
    string CallId,
    string ToolName,
    string Result,
    bool IsError,
    AgentToolReceipt? Receipt = null);

internal static class ToolExecutionResultHistory
{
    public static string ResolveSafeContent(ToolExecutionResult result)
    {
        if (result.Receipt?.Status != AgentToolReceiptStatus.AuthorizationRequired)
            return result.Result;

        var safeMessage = result.Receipt.AuthorizationRequired?.SafeMessage;
        return string.IsNullOrWhiteSpace(safeMessage)
            ? "Authorization is required to use this service."
            : safeMessage.Trim();
    }
}

/// <summary>
/// Streaming tool executor that starts executing tools as soon as they appear,
/// runs read-only tools in parallel, and yields results in call-order.
/// </summary>
// Refactor (iter35/cluster-040-streaming-tool-executor):
//   Old pattern: StreamingToolExecutor owns process-local channel coordinator + TaskCompletionSource waiters + List<TrackedTool>/List<TaskCompletionSource> as object fields for tool execution ordering.
//   New principle: Tool execution state kept in owning chat/actor turn,或 narrow runtime-neutral tool scheduling abstraction(no process-local progress storage)。Streaming tool progress advanced by owning execution flow;process-local channels 仅作 transport mechanics,不作 business progress 来源。
public sealed class StreamingToolExecutor
{
    private const string SafeToolFailureMessage = "The tool request failed.";
    private readonly ToolManager _tools;
    private readonly AgentHookPipeline? _hooks;
    private readonly AgentToolExecutionContext? _toolContext;
    private readonly IAgentToolExecutionPort? _toolExecutionPort;
    private readonly IChatToolCheckpointPort _checkpointPort;
    private readonly AgentToolApprovalContinuationMode _approvalContinuationMode;
    private readonly AgentToolApprovalGrant? _approvalGrant;
    private readonly ILogger _logger;

    public StreamingToolExecutor(
        ToolManager tools,
        AgentHookPipeline? hooks = null,
        IReadOnlyDictionary<string, string>? requestMetadata = null,
        AgentToolExecutionContext? toolContext = null,
        IAgentToolExecutionPort? toolExecutionPort = null,
        IChatToolCheckpointPort? checkpointPort = null,
        AgentToolApprovalContinuationMode approvalContinuationMode = AgentToolApprovalContinuationMode.None,
        AgentToolApprovalGrant? approvalGrant = null,
        ILogger? logger = null)
    {
        // Refactor (issue1574): Old pattern: streaming tool execution promoted request Metadata into tool control.
        // New principle: streaming tool control is typed; request Metadata remains external annotations only.
        _tools = tools;
        _hooks = hooks;
        _toolExecutionPort = toolExecutionPort;
        _checkpointPort = checkpointPort ?? NoOpChatToolCheckpointPort.Instance;
        _approvalContinuationMode = approvalContinuationMode;
        _approvalGrant = approvalGrant;
        _logger = logger ?? NullLogger.Instance;
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

    public async Task<IReadOnlyList<PreparedChatToolOperation>> PrepareBatchAsync(
        string sessionId,
        int round,
        IReadOnlyList<ToolCall> toolCalls,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        if (toolCalls.Count == 0)
            return [];

        var baseContext = _toolContext ?? AgentToolRequestContext.Current ?? AgentToolExecutionContext.Empty;
        var intents = new List<ChatToolOperationIntent>(toolCalls.Count);
        foreach (var toolCall in toolCalls)
        {
            ct.ThrowIfCancellationRequested();
            var frozenCall = CloneToolCall(toolCall);
            var callContext = baseContext.WithCallId(frozenCall.Id);
            var tool = _tools.Get(frozenCall.Name);
            AgentToolReplayPolicy replayPolicy;
            using (AgentToolContextScope.Push(callContext))
            {
                replayPolicy = tool?.ResolveReplayPolicy(frozenCall.ArgumentsJson)
                               ?? AgentToolReplayPolicy.NonReplayable;
            }

            if (replayPolicy == AgentToolReplayPolicy.Unspecified)
                throw new InvalidOperationException("A prepared tool operation requires an explicit replay policy.");

            intents.Add(new ChatToolOperationIntent(
                frozenCall,
                callContext,
                replayPolicy,
                ToolPresentationDescriptors.Snapshot(
                    tool,
                    frozenCall.Name,
                    frozenCall.ArgumentsJson)));
        }

        var prepared = await _checkpointPort.PrepareBatchAsync(
            new ChatToolBatchIntent(sessionId, round, intents),
            ct).ConfigureAwait(false);
        ValidatePreparedBatch(intents, prepared);
        return prepared;
    }

    /// <summary>
    /// Queue a tool for execution. Immediately starts if concurrency rules allow.
    /// If <see cref="Discard"/> has already been called, the tool is recorded as
    /// an immediate discard-error without scheduling.
    /// </summary>
    public void AddTool(ExecutionState state, PreparedChatToolOperation operation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(operation);

        var tool = _tools.Get(operation.ToolCall.Name);
        var tracked = new ToolExecutionEntry(
            Operation: operation,
            Tool: tool,
            IsConcurrencySafe: tool?.IsReadOnly == true && tool.IsDestructive == false);

        state.Tools.Add(tracked);
        if (state.Discarded)
        {
            tracked.Status = ToolStatus.Completed;
                tracked.Result = new ToolExecutionResult(
                    operation.ToolCall.Id,
                    operation.ToolCall.Name,
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
        Advance(state);
        return DrainReadyResults(state)
            .Select(NormalizeFailureReceipt)
            .ToList();
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
            await CommitFinishedToolsAsync(state, ct).ConfigureAwait(false);
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
        ProcessQueue(state);
        PublishAvailableResults(state);
    }

    private async Task CommitFinishedToolsAsync(ExecutionState state, CancellationToken ct)
    {
        foreach (var tracked in state.Tools)
        {
            if (tracked.Status == ToolStatus.Executing &&
                tracked.Execution is { IsCompleted: true } execution)
            {
                ToolExecutionCompletion completion;
                if (execution.IsCanceled && state.DiscardCts.IsCancellationRequested)
                {
                    completion = new ToolExecutionCompletion(
                        new ToolExecutionResult(
                            tracked.Call.Id,
                            tracked.Call.Name,
                            "Tool execution was discarded",
                            IsError: true),
                        SchedulerFault: false);
                }
                else if (execution.IsFaulted)
                {
                    _ = execution.Exception;
                    throw new InvalidOperationException("Tool execution failed before completion was recorded.");
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

            if (tracked.Status != ToolStatus.Completed ||
                tracked.CompletionCommitted ||
                tracked.Result is not { } result)
            {
                continue;
            }

            await _checkpointPort.CommitCompletionAsync(tracked.Operation, result, ct)
                .ConfigureAwait(false);
            tracked.CompletionCommitted = true;
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
                    tracked.Call.Name,
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
            if (tracked.Status != ToolStatus.Completed ||
                !tracked.CompletionCommitted ||
                tracked.Result is not { } result)
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

    private static ToolExecutionResult NormalizeFailureReceipt(ToolExecutionResult result)
    {
        if (!result.IsError || result.Receipt is not null)
            return result;

        return result with
        {
            Receipt = new AgentToolReceipt
            {
                CallId = result.CallId,
                ToolName = result.ToolName,
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = "tool_execution_error",
                ErrorMessage = "The tool request failed.",
                ResultJson = result.Result,
            },
        };
    }

    private static string BuildSafeFailureResult() =>
        ToolManager.BuildErrorJson(SafeToolFailureMessage);

    private static void ValidatePreparedBatch(
        IReadOnlyList<ChatToolOperationIntent> intents,
        IReadOnlyList<PreparedChatToolOperation>? prepared)
    {
        if (prepared is null || prepared.Count != intents.Count)
            throw new InvalidOperationException("The durable tool checkpoint returned an invalid prepared batch.");

        for (var index = 0; index < prepared.Count; index++)
        {
            var expected = intents[index];
            var actual = prepared[index];
            if (string.IsNullOrWhiteSpace(actual.OperationId) ||
                !string.Equals(actual.ExecutionContext.Request.OperationId, actual.OperationId, StringComparison.Ordinal) ||
                actual.ReplayPolicy == AgentToolReplayPolicy.Unspecified ||
                actual.ReplayPolicy != expected.ReplayPolicy ||
                !string.Equals(actual.ToolCall.Id, expected.ToolCall.Id, StringComparison.Ordinal) ||
                !string.Equals(actual.ToolCall.Name, expected.ToolCall.Name, StringComparison.Ordinal) ||
                !string.Equals(actual.ToolCall.ArgumentsJson, expected.ToolCall.ArgumentsJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The durable tool checkpoint altered a prepared operation identity.");
            }
        }
    }

    private static ToolCall CloneToolCall(ToolCall toolCall) => new()
    {
        Id = toolCall.Id,
        Name = toolCall.Name,
        ArgumentsJson = toolCall.ArgumentsJson,
    };

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
                tracked.Call.Name,
                "Tool execution was discarded",
                IsError: true);
        }
    }

    private static bool HasRemainingTools(ExecutionState state) =>
        state.Tools.Any(static tracked => tracked.Status != ToolStatus.Yielded);

    private Task<ToolExecutionCompletion> ExecuteToolAsync(
        CancellationToken ct,
        ToolExecutionEntry tracked) =>
        ExecutePreparedToolAsync(ct, tracked);

    private async Task<ToolExecutionCompletion> ExecutePreparedToolAsync(
        CancellationToken ct,
        ToolExecutionEntry tracked)
    {
        try
        {
            var executionContext = tracked.Operation.ExecutionContext;
            using var _ = AgentToolContextScope.Push(executionContext);

            var call = tracked.Call;
            var toolCtx = new AIGAgentExecutionHookContext
            {
                ToolName = call.Name,
                ToolArguments = call.ArgumentsJson,
                ToolCallId = call.Id,
            };
            try { if (_hooks != null) await _hooks.RunToolExecuteStartAsync(toolCtx, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Tool execution start hook failed for tool {ToolName} and call {CallId}",
                    call.Name,
                    call.Id);
            }
            var toolStartedAt = Stopwatch.GetTimestamp();

            // Re-resolve tool after hooks — hooks may have rewritten the tool name.
            var effectiveToolName = string.IsNullOrWhiteSpace(toolCtx.ToolName) ? call.Name : toolCtx.ToolName!;
            if (!executionContext.ToolVisibility.Allows(effectiveToolName))
            {
                return new ToolExecutionCompletion(
                    new ToolExecutionResult(
                        call.Id,
                        call.Name,
                        ToolManager.BuildErrorJson($"Tool '{effectiveToolName}' is not available in this context."),
                        IsError: true),
                    SchedulerFault: true);
            }

            var effectiveTool = _tools.Get(effectiveToolName) ?? tracked.Tool ?? new NullAgentTool(call.Name);

            if (!string.Equals(effectiveToolName, call.Name, StringComparison.Ordinal) ||
                !string.Equals(toolCtx.ToolArguments, call.ArgumentsJson, StringComparison.Ordinal))
            {
                return new ToolExecutionCompletion(
                    new ToolExecutionResult(
                        call.Id,
                        call.Name,
                        ToolManager.BuildErrorJson("A prepared tool operation cannot be rewritten after its intent is committed."),
                        IsError: true),
                    SchedulerFault: true);
            }

            var executionPort = _toolExecutionPort
                ?? throw new InvalidOperationException("IAgentToolExecutionPort is required for server-owned tool execution.");
            var outcome = await executionPort.ExecuteAsync(
                new AgentToolExecutionRequest(
                    effectiveTool,
                    call.ArgumentsJson,
                    executionContext,
                    _approvalContinuationMode,
                    _approvalGrant,
                    tracked.Operation.ExecutionAttemptKind),
                ct).ConfigureAwait(false);
            var toolResult = outcome.ResultJson;
            var receipt = outcome.Receipt;
            var isErrorReceipt = receipt?.Status is AgentToolReceiptStatus.Error or
                AgentToolReceiptStatus.Denied or
                AgentToolReceiptStatus.AuthorizationRequired or
                AgentToolReceiptStatus.Unspecified;
            var safeToolResult = isErrorReceipt && !string.IsNullOrWhiteSpace(receipt?.ResultJson)
                ? receipt.ResultJson
                : toolResult;

            toolCtx.ToolResult = safeToolResult;
            toolCtx.Duration = Stopwatch.GetElapsedTime(toolStartedAt);
            try { if (_hooks != null) await _hooks.RunToolExecuteEndAsync(toolCtx, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Tool execution end hook failed for tool {ToolName} and call {CallId}",
                    effectiveToolName,
                    call.Id);
            }

            if (ct.IsCancellationRequested)
                return new ToolExecutionCompletion(
                new ToolExecutionResult(call.Id, call.Name, "Tool execution was discarded", IsError: true),
                    SchedulerFault: false);

            return new ToolExecutionCompletion(
                new ToolExecutionResult(
                    call.Id,
                    call.Name,
                    safeToolResult,
                    IsError: isErrorReceipt,
                    Receipt: receipt),
                SchedulerFault: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ToolExecutionCompletion(
                new ToolExecutionResult(
                    tracked.Call.Id,
                    tracked.Call.Name,
                    "Tool execution was discarded",
                    IsError: true),
                SchedulerFault: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Tool execution failed before receipt finalization for tool {ToolName} and call {CallId}",
                tracked.Call.Name,
                tracked.Call.Id);

            return new ToolExecutionCompletion(
                new ToolExecutionResult(
                    tracked.Call.Id,
                    tracked.Call.Name,
                    BuildSafeFailureResult(),
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
            Task.FromException<string>(new InvalidOperationException($"Tool '{name}' was not found."));
    }

    internal enum ToolStatus { Queued, Executing, Completed, Yielded }

    internal sealed class ToolExecutionEntry(
        PreparedChatToolOperation Operation,
        IAgentTool? Tool,
        bool IsConcurrencySafe)
    {
        public PreparedChatToolOperation Operation { get; } = Operation;
        public ToolCall Call => Operation.ToolCall;
        public IAgentTool? Tool { get; } = Tool;
        public bool IsConcurrencySafe { get; } = IsConcurrencySafe;
        public ToolStatus Status { get; set; }
        public ToolExecutionResult? Result { get; set; }
        public Task<ToolExecutionCompletion>? Execution { get; set; }
        public bool CompletionCommitted { get; set; }
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
