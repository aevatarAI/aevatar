namespace Aevatar.Context.Memory;

/// <summary>
/// 记忆提取器接口。
/// 从对话消息中提取 6 类结构化记忆。
/// </summary>
public interface IMemoryExtractor
{
    /// <summary>
    /// 从消息列表中提取候选记忆。
    /// </summary>
    Task<IReadOnlyList<CandidateMemory>> ExtractAsync(
        IReadOnlyList<string> messages,
        CancellationToken ct = default);
}
