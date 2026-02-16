namespace Aevatar.Context.Abstractions;

/// <summary>
/// 一个上下文条目的完整元数据视图。
/// </summary>
public interface IContextEntry
{
    /// <summary>条目的唯一 URI。</summary>
    AevatarUri Uri { get; }

    /// <summary>条目类型。</summary>
    ContextType Type { get; }

    /// <summary>L0 摘要（~100 tokens），可能尚未生成。</summary>
    string? Abstract { get; }

    /// <summary>L1 概览（~2k tokens），可能尚未生成。</summary>
    string? Overview { get; }
}
