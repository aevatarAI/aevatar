namespace Aevatar.Workflow.Core.Primitives;

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
    /// 分支映射（条件 → 下一步 ID），用于条件分支与 switch。
    /// </summary>
    public Dictionary<string, string>? Branches { get; init; }

    /// <summary>
    /// 步骤级重试配置。为 null 表示不重试（失败即终止或走 on_error）。
    /// </summary>
    public StepRetryPolicy? Retry { get; init; }

    /// <summary>
    /// 步骤级错误处理策略。为 null 表示使用默认行为（fail，终止 workflow）。
    /// </summary>
    public StepErrorPolicy? OnError { get; init; }

    /// <summary>
    /// 步骤级超时（毫秒）。为 null 或 0 表示不设超时。
    /// </summary>
    public int? TimeoutMs { get; init; }
}

/// <summary>
/// 步骤级重试策略。
/// </summary>
public sealed class StepRetryPolicy
{
    /// <summary>最大尝试次数（含首次）。最小 1，最大 10。</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>退避策略：fixed 或 exponential。</summary>
    public string Backoff { get; init; } = "fixed";

    /// <summary>基础延迟毫秒数（exponential 时每次翻倍）。</summary>
    public int DelayMs { get; init; } = 1000;
}

/// <summary>
/// 步骤级错误处理策略。
/// </summary>
public sealed class StepErrorPolicy
{
    /// <summary>策略类型：fail | skip | fallback。</summary>
    public string Strategy { get; init; } = "fail";

    /// <summary>当 strategy=fallback 时，跳转到的备用步骤 ID。</summary>
    public string? FallbackStep { get; init; }

    /// <summary>当 strategy=skip 时使用的默认输出值。</summary>
    public string? DefaultOutput { get; init; }
}
