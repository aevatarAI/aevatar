using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Context.Memory;

/// <summary>
/// 基于 LLM 的记忆提取器实现。
/// 分析对话消息，提取 6 类记忆候选项。
/// </summary>
public sealed class LLMMemoryExtractor : IMemoryExtractor
{
    private readonly ILLMProviderFactory _llmFactory;
    private readonly ILogger _logger;

    public LLMMemoryExtractor(
        ILLMProviderFactory llmFactory,
        ILogger<LLMMemoryExtractor>? logger = null)
    {
        _llmFactory = llmFactory;
        _logger = logger ?? NullLogger<LLMMemoryExtractor>.Instance;
    }

    public async Task<IReadOnlyList<CandidateMemory>> ExtractAsync(
        IReadOnlyList<string> messages,
        CancellationToken ct = default)
    {
        if (messages.Count == 0)
            return [];

        var provider = _llmFactory.GetDefault();
        var conversation = string.Join("\n\n", messages);

        var prompt = $$"""
            Analyze the following conversation and extract structured memories.
            
            Categories:
            - profile: User identity/attributes (name, role, organization)
            - preferences: User preferences (coding style, tools, UI preferences)
            - entities: Named entities mentioned (people, projects, technologies)
            - events: Important events/decisions made
            - cases: Problem + solution pairs (what went wrong, how it was fixed)
            - patterns: Reusable patterns or best practices discovered
            
            For each memory, output a JSON object:
            {"category": "...", "content": "...", "source": "brief context"}
            
            Output a JSON array. Output [] if no meaningful memories found.
            Return ONLY valid JSON.
            
            Conversation:
            {{Truncate(conversation, 10000)}}
            """;

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User(prompt)],
            MaxTokens = 2000,
            Temperature = 0.0,
        };

        try
        {
            var response = await provider.ChatAsync(request, ct);
            var json = response.Content?.Trim() ?? "[]";

            if (json.StartsWith("```"))
            {
                var start = json.IndexOf('[');
                var end = json.LastIndexOf(']');
                if (start >= 0 && end > start)
                    json = json[start..(end + 1)];
            }

            var items = JsonSerializer.Deserialize<List<MemoryJson>>(json, JsonOptions);
            if (items == null)
                return [];

            var results = items
                .Where(i => i.Content is { Length: > 0 })
                .Select(i => new CandidateMemory(
                    ParseCategory(i.Category),
                    i.Content!,
                    i.Source ?? ""))
                .ToList();

            _logger.LogInformation("Extracted {Count} memories from {MsgCount} messages",
                results.Count, messages.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory extraction failed");
            return [];
        }
    }

    private static MemoryCategory ParseCategory(string? category) => category?.ToLowerInvariant() switch
    {
        "profile" => MemoryCategory.Profile,
        "preferences" => MemoryCategory.Preferences,
        "entities" => MemoryCategory.Entities,
        "events" => MemoryCategory.Events,
        "cases" => MemoryCategory.Cases,
        "patterns" => MemoryCategory.Patterns,
        _ => MemoryCategory.Events,
    };

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "\n... (truncated)";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class MemoryJson
    {
        public string? Category { get; set; }
        public string? Content { get; set; }
        public string? Source { get; set; }
    }
}
