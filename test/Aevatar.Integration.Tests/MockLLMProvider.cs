// ─────────────────────────────────────────────────────────────
// MockLLMProvider — 测试用 LLM 提供者
// 根据 system prompt 中的角色名返回预设内容
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.LLM;

namespace Aevatar.Integration.Tests;

/// <summary>
/// 测试用 Mock LLM。根据 system prompt 识别角色，返回对应的预设回复。
/// 支持记录调用历史，用于断言。
/// </summary>
public sealed class MockLLMProvider : ILLMProvider, ILLMProviderFactory
{
    /// <summary>调用记录：(system_prompt, user_message) → response。</summary>
    public List<(string SystemPrompt, string UserMessage, string Response)> CallLog { get; } = [];

    /// <summary>预设回复规则：角色关键词 → 回复内容。</summary>
    public Dictionary<string, string> RoleResponses { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["researcher"] = "经过调研，量子纠缠的最新进展包括：1. 远距离量子隐形传态突破 2. 量子互联网原型验证 3. 纠缠态存储时间新纪录",
        ["reviewer"] = "审查意见：研究内容全面，论据充分。建议补充引用最新 Nature 论文。评分：8/10",
        ["writer"] = "# 量子纠缠最新进展综述\n\n## 1. 远距离量子隐形传态\n...\n\n## 2. 量子互联网\n...\n\n## 结论\n该领域正在快速发展。",
        ["default"] = "我是 AI 助手，已收到您的消息。",
    };

    public string Name => "mock";

    public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
    {
        var systemPrompt = request.Messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
        var userMessage = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        // 根据 system prompt 中的关键词选择回复
        var response = RoleResponses["default"];
        foreach (var (key, value) in RoleResponses)
        {
            if (key != "default" && systemPrompt.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                response = value;
                break;
            }
        }

        CallLog.Add((systemPrompt, userMessage, response));

        return Task.FromResult(new LLMResponse
        {
            Content = response,
            FinishReason = "stop",
            Usage = new TokenUsage(100, 50, 150),
        });
    }

    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var full = await ChatAsync(request, ct);
        // 模拟逐字输出
        foreach (var ch in full.Content ?? "")
        {
            yield return new LLMStreamChunk { DeltaContent = ch.ToString() };
        }
        yield return new LLMStreamChunk { IsLast = true, Usage = full.Usage };
    }

    // ─── ILLMProviderFactory 实现 ───

    public ILLMProvider GetProvider(string name) => this;
    public ILLMProvider GetDefault() => this;
    public IReadOnlyList<string> GetAvailableProviders() => ["mock"];
}
