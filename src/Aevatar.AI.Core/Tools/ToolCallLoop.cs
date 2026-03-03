// ─────────────────────────────────────────────────────────────
// ToolCallLoop — Tool Calling 循环逻辑
// LLM 返回 tool_call → 执行 → 将结果加入历史 → 继续调 LLM
// 在每次 LLM 调用和 Tool 执行前后调用 Hook Pipeline + Middleware
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

/// <summary>Tool Calling 循环。含 Hook + Middleware 集成。</summary>
public sealed class ToolCallLoop
{
    private readonly ToolManager _tools;
    private readonly AgentHookPipeline? _hooks;
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;

    public ToolCallLoop(
        ToolManager tools,
        AgentHookPipeline? hooks = null,
        IReadOnlyList<IToolCallMiddleware>? toolMiddlewares = null,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null)
    {
        _tools = tools;
        _hooks = hooks;
        _toolMiddlewares = toolMiddlewares ?? [];
        _llmMiddlewares = llmMiddlewares ?? [];
    }

    /// <summary>
    /// Maximum character count for a single tool result. Results exceeding this
    /// limit are truncated with a notice. Prevents a single large tool response
    /// (e.g. a full graph snapshot) from blowing the context window.
    /// ~200K chars ≈ ~50K tokens — leaves room for system prompt, history, etc.
    /// </summary>
    public int MaxToolResultChars { get; set; } = 200_000;

    /// <summary>
    /// 执行 Tool Calling 循环。返回最终的 LLM 文本内容。
    /// 循环：LLM → tool_call → execute → result → LLM → ...
    /// 每次 LLM 调用和 Tool 执行前后触发 Hook + Middleware。
    /// </summary>
    public async Task<string?> ExecuteAsync(
        ILLMProvider provider, List<ChatMessage> messages,
        LLMRequest baseRequest, int maxRounds,
        CancellationToken ct)
    {
        for (var round = 0; round < maxRounds; round++)
        {
            var request = new LLMRequest
            {
                Messages = [..messages], Tools = baseRequest.Tools,
                Model = baseRequest.Model, Temperature = baseRequest.Temperature,
                MaxTokens = baseRequest.MaxTokens,
            };

            var (response, terminated) = await InvokeLlmAsync(provider, request, ct);

            if (terminated || !response.HasToolCalls)
            {
                if (response.Content != null)
                    messages.Add(ChatMessage.Assistant(response.Content));

                return response.Content;
            }

            // 记录 assistant tool_call 消息（保留 Content，部分 LLM 会同时返回文本和 tool_call）
            messages.Add(new ChatMessage { Role = "assistant", Content = response.Content, ToolCalls = response.ToolCalls });

            // 执行每个 tool call（委托给可复用方法）
            var toolResults = await ExecuteToolCallsAsync(response.ToolCalls!, ct);
            foreach (var (callId, _, resultContent) in toolResults)
                messages.Add(ChatMessage.Tool(callId, resultContent));
        }

        // maxRounds exhausted — tool results from the last round are already in messages.
        // Make one final LLM call WITHOUT tools so the model must produce a text response.
        var finalRequest = new LLMRequest
        {
            Messages = [..messages], Tools = null,
            Model = baseRequest.Model, Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
        };
        var (finalResponse, _) = await InvokeLlmAsync(provider, finalRequest, ct);
        var finalContent = finalResponse?.Content;
        if (finalContent != null)
            messages.Add(ChatMessage.Assistant(finalContent));
        return finalContent;
    }

    /// <summary>
    /// Execute a list of tool calls with hooks, middleware, and truncation.
    /// Returns the results without invoking the LLM — reusable by both sync and streaming paths.
    /// </summary>
    public async Task<List<(string CallId, string ToolName, string Result)>> ExecuteToolCallsAsync(
        IReadOnlyList<ToolCall> toolCalls, CancellationToken ct)
    {
        var results = new List<(string CallId, string ToolName, string Result)>(toolCalls.Count);

        foreach (var call in toolCalls)
        {
            // ─── Hook: Tool Execute Start ───
            var toolCtx = new AIGAgentExecutionHookContext
            {
                ToolName = call.Name, ToolArguments = call.ArgumentsJson, ToolCallId = call.Id,
            };
            if (_hooks != null) await _hooks.RunToolExecuteStartAsync(toolCtx, ct);

            var tool = _tools.Get(call.Name);
            var toolCallContext = new ToolCallContext
            {
                Tool = tool ?? new NullAgentTool(call.Name),
                ToolName = string.IsNullOrWhiteSpace(toolCtx.ToolName) ? call.Name : toolCtx.ToolName!,
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

            var toolResult = toolCallContext.Result ?? $"Tool '{toolCallContext.ToolName}' returned no result";

            // Truncate oversized tool results to prevent context-window overflow
            if (!toolCallContext.Terminate && toolResult.Length > MaxToolResultChars)
            {
                toolResult = string.Concat(
                    toolResult.AsSpan(0, MaxToolResultChars),
                    $"\n\n[TRUNCATED — tool result was {toolResult.Length:N0} chars, limit is {MaxToolResultChars:N0}]");
            }

            if (toolCallContext.Terminate)
                toolResult = toolCallContext.Result ?? "Tool call terminated by middleware";

            // ─── Hook: Tool Execute End ───
            toolCtx.ToolResult = toolResult;
            if (_hooks != null) await _hooks.RunToolExecuteEndAsync(toolCtx, ct);

            results.Add((call.Id, call.Name, toolResult));
        }

        return results;
    }

    private async Task<(LLMResponse Response, bool Terminated)> InvokeLlmAsync(
        ILLMProvider provider,
        LLMRequest request,
        CancellationToken ct)
    {
        // ─── Hook: LLM Request Start ───
        var llmCtx = new AIGAgentExecutionHookContext { LLMRequest = request };
        if (_hooks != null) await _hooks.RunLLMRequestStartAsync(llmCtx, ct);

        var llmCallContext = new LLMCallContext
        {
            Request = request,
            Provider = provider,
            CancellationToken = ct,
            IsStreaming = false,
        };

        await MiddlewarePipeline.RunLLMCallAsync(_llmMiddlewares, llmCallContext, async () =>
        {
            if (llmCallContext.Terminate) return;
            llmCallContext.Response = await provider.ChatAsync(llmCallContext.Request, ct);
        });

        var response = llmCallContext.Response
            ?? new LLMResponse { Content = null, ToolCalls = null };
        llmCtx.LLMResponse = response;

        // ─── Hook: LLM Request End ───
        if (_hooks != null) await _hooks.RunLLMRequestEndAsync(llmCtx, ct);

        return (response, llmCallContext.Terminate);
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
}
