// ─────────────────────────────────────────────────────────────
// WorkflowParser — YAML 工作流解析器
// 将 YAML 文本反序列化为 WorkflowDefinition 及嵌套步骤结构
// ─────────────────────────────────────────────────────────────

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// YAML 工作流解析器。使用 snake_case 命名约定将 YAML 解析为强类型工作流定义。
/// </summary>
public sealed class WorkflowParser
{
    private static readonly IDeserializer D = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance).Build();

    /// <summary>
    /// 解析 YAML 字符串为工作流定义。
    /// </summary>
    /// <param name="yaml">YAML 格式的工作流配置文本。</param>
    /// <returns>解析后的工作流定义。</returns>
    /// <exception cref="InvalidOperationException">YAML 为空或缺少必填字段时抛出。</exception>
    public WorkflowDefinition Parse(string yaml)
    {
        var raw = D.Deserialize<Raw>(yaml) ?? throw new InvalidOperationException("YAML 为空");
        return new WorkflowDefinition
        {
            Name = raw.Name ?? throw new InvalidOperationException("缺少 name"),
            Description = raw.Description ?? "",
            Roles = (raw.Roles ?? []).Select(r => new RoleDefinition
            {
                Id = r.Id ?? r.Name ?? "",
                Name = r.Name ?? r.Id ?? "",
                SystemPrompt = r.SystemPrompt ?? "",
                Provider = r.Provider,
                Model = r.Model,
                EventModules = r.EventModules,
                Connectors = r.Connectors ?? [],
            }).ToList(),
            Steps = (raw.Steps ?? []).Select(MapStep).ToList(),
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = raw.Configuration?.ClosedWorldMode ?? false,
            },
        };
    }

    private static StepDefinition MapStep(RawStep s) => new()
    {
        Id = s.Id ?? throw new InvalidOperationException("step 缺 id"),
        Type = s.Type ?? "llm_call", TargetRole = s.TargetRole ?? s.Role,
        Parameters = s.Parameters ?? [], Next = s.Next,
        Children = s.Children?.Select(MapStep).ToList(), Branches = s.Branches,
        Retry = MapRetry(s.Retry), OnError = MapOnError(s.OnError), TimeoutMs = s.TimeoutMs,
    };

    private static StepRetryPolicy? MapRetry(RawRetry? r) =>
        r == null ? null : new StepRetryPolicy
        {
            MaxAttempts = r.MaxAttempts ?? 3,
            Backoff = r.Backoff ?? "fixed",
            DelayMs = r.DelayMs ?? 1000,
        };

    private static StepErrorPolicy? MapOnError(RawOnError? e) =>
        e == null ? null : new StepErrorPolicy
        {
            Strategy = e.Strategy ?? "fail",
            FallbackStep = e.FallbackStep,
            DefaultOutput = e.DefaultOutput,
        };

    private sealed class Raw { public string? Name { get; set; } public string? Description { get; set; } public List<RawRole>? Roles { get; set; } public List<RawStep>? Steps { get; set; } public RawConfiguration? Configuration { get; set; } }
    private sealed class RawRole { public string? Id { get; set; } public string? Name { get; set; } public string? SystemPrompt { get; set; } public string? Provider { get; set; } public string? Model { get; set; } public string? EventModules { get; set; } public List<string>? Connectors { get; set; } }
    private sealed class RawStep { public string? Id { get; set; } public string? Type { get; set; } public string? TargetRole { get; set; } public string? Role { get; set; } public Dictionary<string, string>? Parameters { get; set; } public string? Next { get; set; } public List<RawStep>? Children { get; set; } public Dictionary<string, string>? Branches { get; set; } public RawRetry? Retry { get; set; } public RawOnError? OnError { get; set; } public int? TimeoutMs { get; set; } }
    private sealed class RawRetry { public int? MaxAttempts { get; set; } public string? Backoff { get; set; } public int? DelayMs { get; set; } }
    private sealed class RawOnError { public string? Strategy { get; set; } public string? FallbackStep { get; set; } public string? DefaultOutput { get; set; } }
    private sealed class RawConfiguration { public bool? ClosedWorldMode { get; set; } }
}
