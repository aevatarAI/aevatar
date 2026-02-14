// ─────────────────────────────────────────────────────────────
// StepDefinition — 工作流步骤定义
// 描述单个执行步骤的 ID、类型、目标角色、参数及后继关系
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Workflows.Core.Primitives;

/// <summary>
/// 工作流步骤定义。描述单个步骤的元数据、执行参数及流程控制。
/// </summary>
public sealed class StepDefinition
{
    /// <summary>
    /// 步骤唯一标识符。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 步骤类型（如 llm_call、parallel、loop、conditional 等）。
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// 目标角色 ID，指定该步骤由哪个角色执行。
    /// </summary>
    public string? TargetRole { get; init; }

    /// <summary>
    /// 步骤执行参数键值对。
    /// </summary>
    public Dictionary<string, string> Parameters { get; init; } = [];

    /// <summary>
    /// 下一步骤 ID，用于线性流程控制。
    /// </summary>
    public string? Next { get; init; }

    /// <summary>
    /// 子步骤列表，用于并行或嵌套结构。
    /// </summary>
    public List<StepDefinition>? Children { get; init; }

    /// <summary>
    /// 分支映射（条件 → 下一步 ID），用于条件分支。
    /// </summary>
    public Dictionary<string, string>? Branches { get; init; }
}
