// ─────────────────────────────────────────────────────────────
// WorkflowDefinition — 工作流定义
// 包含角色列表与步骤列表，提供入口步骤及后继步骤查询
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Cognitive.Primitives;

/// <summary>
/// 工作流定义。描述完整工作流的名称、角色、步骤及流程拓扑。
/// </summary>
public sealed class WorkflowDefinition
{
    /// <summary>
    /// 工作流名称。
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 工作流描述。
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// 参与该工作流的角色定义列表。
    /// </summary>
    public required List<RoleDefinition> Roles { get; init; }

    /// <summary>
    /// 步骤定义列表，按执行顺序排列。
    /// </summary>
    public required List<StepDefinition> Steps { get; init; }

    /// <summary>
    /// 入口步骤 ID，即第一个步骤的 ID；若无步骤则为 null。
    /// </summary>
    public string? EntryStepId => Steps.Count > 0 ? Steps[0].Id : null;

    /// <summary>
    /// 根据 ID 查找步骤。
    /// </summary>
    /// <param name="id">步骤 ID。</param>
    /// <returns>对应的步骤定义，未找到则返回 null。</returns>
    public StepDefinition? GetStep(string id) => Steps.Find(s => s.Id == id);

    /// <summary>
    /// 获取当前步骤的后继步骤。
    /// 优先使用步骤的 Next 字段，否则按顺序取下一项。
    /// </summary>
    /// <param name="currentId">当前步骤 ID。</param>
    /// <returns>后继步骤定义，若无则返回 null。</returns>
    public StepDefinition? GetNextStep(string currentId)
    {
        var s = GetStep(currentId);
        if (s?.Next != null) return GetStep(s.Next);
        var idx = Steps.FindIndex(x => x.Id == currentId);
        return idx >= 0 && idx + 1 < Steps.Count ? Steps[idx + 1] : null;
    }
}

/// <summary>
/// 角色定义。描述工作流中参与者的 ID、名称、系统提示及模型配置。
/// </summary>
public sealed class RoleDefinition
{
    /// <summary>
    /// 角色唯一标识符。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 角色显示名称。
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 系统提示词，用于 LLM 调用时的角色设定。
    /// </summary>
    public string SystemPrompt { get; init; } = "";

    /// <summary>
    /// LLM 服务提供商标识。
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// 使用的模型名称。
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// 该角色绑定的事件模块列表（逗号分隔）。
    /// </summary>
    public string? EventModules { get; init; }

    /// <summary>
    /// 该角色允许使用的 Connector 名称列表（中心化配置在 ~/.aevatar/connectors.json）。
    /// 当 connector_call 步骤指定本角色时，仅允许调用此列表中的 connector。
    /// </summary>
    public List<string> Connectors { get; init; } = [];
}
