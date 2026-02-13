// ─────────────────────────────────────────────────────────────
// WorkflowValidator — 工作流校验器
// 校验工作流定义的完整性（名称、步骤、角色引用、后继引用）
// ─────────────────────────────────────────────────────────────

using Aevatar.Cognitive.Primitives;

namespace Aevatar.Cognitive.Validation;

/// <summary>
/// 工作流校验器。对工作流定义进行静态校验，返回错误信息列表。
/// </summary>
public static class WorkflowValidator
{
    /// <summary>
    /// 校验工作流定义。
    /// 检查 name 非空、至少一个步骤、步骤 ID 不重复、角色引用有效、next 引用有效。
    /// </summary>
    /// <param name="wf">待校验的工作流定义。</param>
    /// <returns>错误消息列表，无错误时返回空列表。</returns>
    public static List<string> Validate(WorkflowDefinition wf)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(wf.Name)) errors.Add("缺少 name");
        if (wf.Steps.Count == 0) errors.Add("至少需要一个 step");

        var stepIds = new HashSet<string>();
        foreach (var s in wf.Steps) if (!stepIds.Add(s.Id)) errors.Add($"步骤 '{s.Id}' 重复");

        var roleIds = wf.Roles.Select(r => r.Id).ToHashSet();
        foreach (var s in wf.Steps)
        {
            if (s.TargetRole != null && !roleIds.Contains(s.TargetRole))
                errors.Add($"步骤 '{s.Id}' 引用不存在的角色 '{s.TargetRole}'");
            if (s.Next != null && !stepIds.Contains(s.Next))
                errors.Add($"步骤 '{s.Id}' 的 next 引用不存在的步骤 '{s.Next}'");
        }
        return errors;
    }
}
