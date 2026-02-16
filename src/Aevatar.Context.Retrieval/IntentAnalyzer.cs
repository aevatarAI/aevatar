using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Context.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Context.Retrieval;

/// <summary>
/// LLM 意图分析器。
/// 将用户查询分析为 0-5 个 TypedQuery，每个指向特定上下文类型。
/// </summary>
public sealed class IntentAnalyzer
{
    private readonly ILLMProviderFactory _llmFactory;
    private readonly ILogger _logger;

    public IntentAnalyzer(
        ILLMProviderFactory llmFactory,
        ILogger<IntentAnalyzer>? logger = null)
    {
        _llmFactory = llmFactory;
        _logger = logger ?? NullLogger<IntentAnalyzer>.Instance;
    }

    /// <summary>
    /// 分析查询意图，生成 0-5 个 TypedQuery。
    /// 0 个表示闲聊/问候，不需要检索。
    /// </summary>
    public async Task<IReadOnlyList<TypedQuery>> AnalyzeAsync(
        string query,
        SessionInfo? session = null,
        CancellationToken ct = default)
    {
        var provider = _llmFactory.GetDefault();

        var contextSection = "";
        if (session is { RecentMessages.Count: > 0 })
        {
            var recentText = string.Join("\n", session.RecentMessages.TakeLast(5));
            contextSection = $"\nRecent conversation:\n{recentText}\n";
        }

        var prompt = $"""
            Analyze the following user query and generate retrieval queries.
            {contextSection}
            User query: "{query}"

            For each retrieval need, output a JSON object with:
            - "query": rewritten search query (skill queries use verb-first, resource queries use noun phrases, memory queries use "user's X")
            - "context_type": one of "resource", "memory", "skill"
            - "intent": brief purpose description
            - "priority": 1-5 (1 = highest)

            Output a JSON array of 0-5 objects. Output [] for greetings/chitchat.
            Return ONLY valid JSON, no other text.
            """;

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User(prompt)],
            MaxTokens = 500,
            Temperature = 0.0,
        };

        try
        {
            var response = await provider.ChatAsync(request, ct);
            var json = response.Content?.Trim() ?? "[]";

            // Strip markdown code fences if present
            if (json.StartsWith("```"))
            {
                var start = json.IndexOf('[');
                var end = json.LastIndexOf(']');
                if (start >= 0 && end > start)
                    json = json[start..(end + 1)];
            }

            var items = JsonSerializer.Deserialize<List<TypedQueryJson>>(json, JsonOptions);
            if (items == null)
                return [];

            var results = items
                .Where(i => i.Query != null && i.ContextType != null)
                .Select(i => new TypedQuery(
                    i.Query!,
                    ParseContextType(i.ContextType!),
                    i.Intent ?? "",
                    Math.Clamp(i.Priority, 1, 5)))
                .OrderBy(q => q.Priority)
                .Take(5)
                .ToList();

            _logger.LogDebug("Intent analysis: {Query} → {Count} typed queries", query, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intent analysis failed for: {Query}", query);
            return [new TypedQuery(query, ContextType.Resource, "fallback", 1)];
        }
    }

    private static ContextType ParseContextType(string type) => type.ToLowerInvariant() switch
    {
        "skill" => ContextType.Skill,
        "memory" => ContextType.Memory,
        _ => ContextType.Resource,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class TypedQueryJson
    {
        public string? Query { get; set; }
        public string? ContextType { get; set; }
        public string? Intent { get; set; }
        public int Priority { get; set; } = 3;
    }
}
