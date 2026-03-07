namespace Aevatar.Workflow.Core.Validation;

public sealed class WorkflowValidationOptions
{
    public static WorkflowValidationOptions Default { get; } = new();

    /// <summary>
    /// 强制覆盖 workflow 自身的 closed world 配置。null 表示使用 workflow 配置值。
    /// </summary>
    public bool? ForceClosedWorldMode { get; init; }

    /// <summary>
    /// 当提供 availableWorkflowNames 时，是否强制校验 workflow_call 目标可解析。
    /// </summary>
    public bool RequireResolvableWorkflowCallTargets { get; init; } = true;

    /// <summary>
    /// 当 KnownStepTypes 提供且非空时，是否强制校验 step type（含 step type 参数位）必须可解析。
    /// </summary>
    public bool RequireKnownStepTypes { get; init; }

    /// <summary>
    /// 已注册的原语名称集合（可包含别名，校验时会 canonicalize）。
    /// </summary>
    public ISet<string>? KnownStepTypes { get; init; }
}
