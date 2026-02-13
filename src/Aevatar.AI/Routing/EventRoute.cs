// ─────────────────────────────────────────────────────────────
// EventRoute — 事件路由规则
//
// 从 YAML 解析路由规则，支持两种格式：
// 1. YAML list:  - when: event.type == "ChatRequestEvent"
//                  to: llm_handler
// 2. 行式 DSL:    event.type == ChatRequestEvent -> llm_handler
//
// 匹配条件：event.type（payload 类型名）或 event.step_type（步骤类型）
// ─────────────────────────────────────────────────────────────

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aevatar.AI.Routing;

/// <summary>
/// 事件路由规则。匹配 event.type 或 event.step_type，路由到指定模块。
/// </summary>
public readonly record struct EventRoute(string? EventType, string? StepType, string TargetModule)
{
    /// <summary>
    /// 检查事件是否匹配此路由规则。
    /// </summary>
    public bool Matches(Aevatar.EventEnvelope envelope, IEventRouteEvaluator evaluator)
    {
        // event.type 匹配
        if (!string.IsNullOrWhiteSpace(EventType))
        {
            if (!evaluator.TryGetEventType(envelope, out var actualType)) return false;
            if (!string.Equals(actualType, EventType, StringComparison.OrdinalIgnoreCase)) return false;
        }

        // event.step_type 匹配
        if (!string.IsNullOrWhiteSpace(StepType))
        {
            if (!evaluator.TryGetStepType(envelope, out var actualStepType)) return false;
            if (!string.Equals(actualStepType, StepType, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    // ─── 解析 ───

    /// <summary>
    /// 从 YAML 字符串解析路由规则数组。
    /// 支持 YAML list 格式和行式 DSL 格式。
    /// </summary>
    public static EventRoute[] Parse(string? raw, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        // 尝试 YAML list 格式
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var list = deserializer.Deserialize<List<Dictionary<string, string>>>(raw);
            if (list is { Count: > 0 })
                return FromYamlList(list, logger);
        }
        catch { /* 回退到行式解析 */ }

        // 行式 DSL 格式
        return ParseLines(raw, logger);
    }

    // ─── YAML list 格式 ───
    // - when: event.type == "ChatRequestEvent"
    //   to: llm_handler

    private static EventRoute[] FromYamlList(List<Dictionary<string, string>> list, ILogger? logger)
    {
        var routes = new List<EventRoute>();
        foreach (var item in list)
        {
            if (!item.TryGetValue("when", out var whenRaw) ||
                !item.TryGetValue("to", out var toRaw))
                continue;

            if (TryParseWhen(whenRaw, out var eventType, out var stepType))
                routes.Add(new EventRoute(eventType, stepType, toRaw.Trim()));
            else
                logger?.LogWarning("[EventRoute] 不支持的条件: {When}", whenRaw);
        }
        return routes.ToArray();
    }

    // ─── 行式 DSL 格式 ───
    // event.type == ChatRequestEvent -> llm_handler

    private static EventRoute[] ParseLines(string raw, ILogger? logger)
    {
        var routes = new List<EventRoute>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var parts = trimmed.Split("->", 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;

            if (TryParseWhen(parts[0], out var eventType, out var stepType))
                routes.Add(new EventRoute(eventType, stepType, parts[1].Trim()));
            else
                logger?.LogWarning("[EventRoute] 不支持的条件: {When}", parts[0]);
        }
        return routes.ToArray();
    }

    // ─── when 条件解析 ───
    // 支持: event.type == "xxx" 和 event.step_type == "xxx"

    private static bool TryParseWhen(string whenRaw, out string? eventType, out string? stepType)
    {
        eventType = null;
        stepType = null;
        var input = whenRaw.Trim();

        var match = Regex.Match(input, @"event\.type\s*==?\s*[""']?(?<v>[^""'\s]+)[""']?", RegexOptions.IgnoreCase);
        if (match.Success) eventType = match.Groups["v"].Value.Trim();

        match = Regex.Match(input, @"event\.step_type\s*==?\s*[""']?(?<v>[^""'\s]+)[""']?", RegexOptions.IgnoreCase);
        if (match.Success) stepType = match.Groups["v"].Value.Trim();

        return !string.IsNullOrWhiteSpace(eventType) || !string.IsNullOrWhiteSpace(stepType);
    }
}
