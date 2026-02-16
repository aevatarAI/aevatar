namespace Aevatar.Context.Memory;

/// <summary>
/// 记忆分类。6 类记忆，分属 user 和 agent 两个作用域。
/// </summary>
public enum MemoryCategory
{
    /// <summary>用户身份属性（user scope，可合并）。</summary>
    Profile,

    /// <summary>用户偏好（user scope，可合并）。</summary>
    Preferences,

    /// <summary>实体记忆：人物/项目/组织（user scope，可合并）。</summary>
    Entities,

    /// <summary>事件记忆：决策/里程碑（user scope，不可修改）。</summary>
    Events,

    /// <summary>案例记忆：问题+解决方案（agent scope，不可修改）。</summary>
    Cases,

    /// <summary>模式记忆：可复用模式/经验（agent scope，可合并）。</summary>
    Patterns,
}

/// <summary>
/// 提取出的候选记忆条目。
/// </summary>
public sealed record CandidateMemory(
    MemoryCategory Category,
    string Content,
    string Source);
