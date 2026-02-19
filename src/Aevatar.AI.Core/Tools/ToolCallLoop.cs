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
    /// 执行 Tool Calling 循环。返回最终的 LLM 文本内容。
    /// 循环：LLM → tool_call → execute → result → LLM → ...
    /// 每次 LLM 调用和 Tool 执行前后触发 Hook + Middleware。
    /// </summary>
    public async Task<string?> ExecuteAsync(
        ILLMProvider provider, List<ChatMessage> messages,
        LLMRequest baseRequest, int maxRounds, CancellationToken ct)
    {
        for (var round = 0; round < maxRounds; round++)
        {
            var request = new LLMRequest
            {
                Messages = [..messages], Tools = baseRequest.Tools,
                Model = baseRequest.Model, Temperature = baseRequest.Temperature,
                MaxTokens = baseRequest.MaxTokens,
            };

            // ─── Hook: LLM Request Start ───
            var llmCtx = new AIGAgentExecutionHookContext { LLMRequest = request };
            if (_hooks != null) await _hooks.RunLLMRequestStartAsync(llmCtx, ct);

            // ─── Middleware + LLM Call ───
            var llmCallContext = new LLMCallContext
            {
                Request = request, Provider = provider,
                CancellationToken = ct, IsStreaming = false,
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

            if (llmCallContext.Terminate || !response.HasToolCalls)
            {
                if (response.Content != null)
                    messages.Add(ChatMessage.Assistant(response.Content));
                return response.Content;
            }

            messages.Add(new ChatMessage { Role = "assistant", ToolCalls = response.ToolCalls });

            foreach (var call in response.ToolCalls!)
            {
                // ─── Hook: Tool Execute Start ───
                var toolCtx = new AIGAgentExecutionHookContext
                {
                    ToolName = call.Name, ToolArguments = call.ArgumentsJson, ToolCallId = call.Id,
                };
                if (_hooks != null) await _hooks.RunToolExecuteStartAsync(toolCtx, ct);

                // ─── Middleware + Tool Execution ───
                var tool = _tools.Get(call.Name);
                var toolCallContext = new ToolCallContext
                {
                    Tool = tool ?? new NullAgentTool(call.Name),
                    ToolName = call.Name,
                    ToolCallId = call.Id,
                    ArgumentsJson = call.ArgumentsJson,
                    CancellationToken = ct,
                };

                await MiddlewarePipeline.RunToolCallAsync(_toolMiddlewares, toolCallContext, async () =>
                {
                    if (toolCallContext.Terminate) return;
                    var result = await _tools.ExecuteToolCallAsync(call, ct);
                    toolCallContext.Result = result.Content;
                });

                var toolResult = toolCallContext.Result ?? $"Tool '{call.Name}' returned no result";

                if (toolCallContext.Terminate)
                    messages.Add(ChatMessage.Tool(call.Id, toolCallContext.Result ?? "Tool call terminated by middleware"));
                else
                    messages.Add(ChatMessage.Tool(call.Id, toolResult));

                // ─── Hook: Tool Execute End ───
                toolCtx.ToolResult = toolResult;
                if (_hooks != null) await _hooks.RunToolExecuteEndAsync(toolCtx, ct);
            }
        }

        return messages.LastOrDefault(m => m.Role == "assistant")?.Content;
    }

    /// <summary>Placeholder tool for middleware when tool is not found.</summary>
    private sealed class NullAgentTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => "";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct) =>
            Task.FromResult($"Tool '{name}' not found");
    }
}
