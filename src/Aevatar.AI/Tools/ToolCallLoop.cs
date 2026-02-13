// ─────────────────────────────────────────────────────────────
// ToolCallLoop — Tool Calling 循环逻辑
// LLM 返回 tool_call → 执行 → 将结果加入历史 → 继续调 LLM
// 在每次 LLM 调用和 Tool 执行前后调用 Hook Pipeline
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Hooks;
using Aevatar.AI.LLM;

namespace Aevatar.AI.Tools;

/// <summary>Tool Calling 循环。含 Hook 集成。</summary>
public sealed class ToolCallLoop
{
    private readonly ToolManager _tools;
    private readonly AgentHookPipeline? _hooks;

    public ToolCallLoop(ToolManager tools, AgentHookPipeline? hooks = null)
    {
        _tools = tools;
        _hooks = hooks;
    }

    /// <summary>
    /// 执行 Tool Calling 循环。返回最终的 LLM 文本内容。
    /// 循环：LLM → tool_call → execute → result → LLM → ...
    /// 每次 LLM 调用和 Tool 执行前后触发 Hook。
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
            var llmCtx = new AIHookContext { LlmRequest = request };
            if (_hooks != null) await _hooks.RunLLMRequestStartAsync(llmCtx, ct);

            var response = await provider.ChatAsync(request, ct);
            llmCtx.LlmResponse = response;

            // ─── Hook: LLM Request End ───
            if (_hooks != null) await _hooks.RunLLMRequestEndAsync(llmCtx, ct);

            if (!response.HasToolCalls)
            {
                if (response.Content != null)
                    messages.Add(ChatMessage.Assistant(response.Content));
                return response.Content;
            }

            // 记录 assistant tool_call 消息
            messages.Add(new ChatMessage { Role = "assistant", ToolCalls = response.ToolCalls });

            // 执行每个 tool call
            foreach (var call in response.ToolCalls!)
            {
                // ─── Hook: Tool Execute Start ───
                var toolCtx = new AIHookContext
                {
                    ToolName = call.Name, ToolArguments = call.ArgumentsJson, ToolCallId = call.Id,
                };
                if (_hooks != null) await _hooks.RunToolExecuteStartAsync(toolCtx, ct);

                var result = await _tools.ExecuteToolCallAsync(call, ct);
                messages.Add(result);

                // ─── Hook: Tool Execute End ───
                toolCtx.ToolResult = result.Content;
                if (_hooks != null) await _hooks.RunToolExecuteEndAsync(toolCtx, ct);
            }
        }

        return messages.LastOrDefault(m => m.Role == "assistant")?.Content;
    }
}
