namespace Aevatar.Context.Abstractions;

/// <summary>
/// 上下文条目的类型分类。
/// </summary>
public enum ContextType
{
    /// <summary>外部知识资源（文档、代码库、网页）。</summary>
    Resource,

    /// <summary>记忆（用户偏好、实体、事件、Agent 经验）。</summary>
    Memory,

    /// <summary>可调用技能。</summary>
    Skill,
}
