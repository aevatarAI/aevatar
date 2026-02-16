using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.Context.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Context.Retrieval;

/// <summary>
/// LLM 调用中间件：在每次 LLM 调用前自动检索相关上下文并注入到消息中。
/// 实现 ILLMCallMiddleware，接入 ToolCallLoop 的中间件管线。
///
/// 工作流程：
/// 1. 从 Request.Messages 中提取最后一条 user 消息作为查询
/// 2. 调用 IContextRetriever.FindAsync 检索 Top-K 相关上下文
/// 3. 将结果格式化为 system 消息，插入到 system prompt 之后
/// 4. 继续管线执行
/// </summary>
public sealed class ContextInjectionMiddleware : ILLMCallMiddleware
{
    private const string MetadataKey = "aevatar.context.injected";
    private const int MaxContextTokenBudget = 3000;

    private readonly IContextRetriever _retriever;
    private readonly IContextStore _store;
    private readonly ILogger _logger;

    public ContextInjectionMiddleware(
        IContextRetriever retriever,
        IContextStore store,
        ILogger<ContextInjectionMiddleware>? logger = null)
    {
        _retriever = retriever;
        _store = store;
        _logger = logger ?? NullLogger<ContextInjectionMiddleware>.Instance;
    }

    public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
    {
        if (context.Metadata.ContainsKey(MetadataKey))
        {
            await next();
            return;
        }

        var userQuery = ExtractLastUserQuery(context.Request.Messages);
        if (userQuery == null)
        {
            await next();
            return;
        }

        try
        {
            var findResult = await _retriever.FindAsync(userQuery, ct: context.CancellationToken);

            if (findResult.Total > 0)
            {
                var contextMessage = await FormatContextMessageAsync(findResult, context.CancellationToken);
                if (contextMessage != null)
                {
                    InjectContextMessage(context.Request.Messages, contextMessage);
                    _logger.LogDebug(
                        "Injected context: {Skills} skills, {Resources} resources, {Memories} memories",
                        findResult.Skills.Count, findResult.Resources.Count, findResult.Memories.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Context injection failed, proceeding without context");
        }

        context.Metadata[MetadataKey] = true;
        await next();
    }

    private static string? ExtractLastUserQuery(List<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == "user" && !string.IsNullOrWhiteSpace(messages[i].Content))
                return messages[i].Content;
        }
        return null;
    }

    private async Task<ChatMessage?> FormatContextMessageAsync(
        FindResult result,
        CancellationToken ct)
    {
        var sections = new List<string>();
        var budget = new TokenBudget(MaxContextTokenBudget * 4);

        if (result.Memories.Count > 0)
        {
            var memorySection = FormatSection("User Memory", result.Memories, budget);
            if (memorySection != null)
                sections.Add(memorySection);
        }

        if (result.Skills.Count > 0)
        {
            var skillSection = FormatSection("Available Skills", result.Skills, budget);
            if (skillSection != null)
                sections.Add(skillSection);
        }

        if (result.Resources.Count > 0)
        {
            var resourceSection = await FormatResourceSectionAsync(result.Resources, ct, budget);
            if (resourceSection != null)
                sections.Add(resourceSection);
        }

        if (sections.Count == 0)
            return null;

        var body = string.Join("\n\n", sections);
        var content = $"""
            [Retrieved Context]
            The following context was automatically retrieved and may be relevant to the user's request.
            Use it if helpful, ignore if not relevant.

            {body}
            """;

        return ChatMessage.System(content);
    }

    private static string? FormatSection(
        string title,
        IReadOnlyList<MatchedContext> items,
        TokenBudget budget)
    {
        if (items.Count == 0)
            return null;

        var lines = new List<string> { $"### {title}" };
        foreach (var item in items)
        {
            var line = $"- [{item.Uri}] (score: {item.Score:F2}): {item.Abstract}";
            if (!budget.TryConsume(line.Length))
                break;
            lines.Add(line);
        }

        return lines.Count > 1 ? string.Join("\n", lines) : null;
    }

    private async Task<string?> FormatResourceSectionAsync(
        IReadOnlyList<MatchedContext> items,
        CancellationToken ct,
        TokenBudget budget)
    {
        var lines = new List<string> { "### Relevant Resources" };
        foreach (var item in items)
        {
            string? detail = null;

            if (!item.IsDirectory)
            {
                try
                {
                    detail = await _store.GetOverviewAsync(item.Uri.Parent, ct);
                }
                catch
                {
                    // Overview not available
                }
            }

            var line = detail != null
                ? $"- [{item.Uri}] (score: {item.Score:F2}): {item.Abstract}\n  Overview: {Truncate(detail, 500)}"
                : $"- [{item.Uri}] (score: {item.Score:F2}): {item.Abstract}";

            if (!budget.TryConsume(line.Length))
                break;
            lines.Add(line);
        }

        return lines.Count > 1 ? string.Join("\n", lines) : null;
    }

    private sealed class TokenBudget(int maxChars)
    {
        private int _consumed;
        public bool TryConsume(int chars)
        {
            if (_consumed + chars > maxChars) return false;
            _consumed += chars;
            return true;
        }
    }

    private static void InjectContextMessage(List<ChatMessage> messages, ChatMessage contextMessage)
    {
        var insertIndex = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == "system")
            {
                insertIndex = i + 1;
                break;
            }
        }
        messages.Insert(insertIndex, contextMessage);
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "...";
}
