using Aevatar.Context.Abstractions;

namespace Aevatar.Context.Extraction;

/// <summary>
/// L0/L1 摘要生成器接口。
/// L0 (Abstract): ~100 tokens 的单句摘要，用于向量检索和快速过滤。
/// L1 (Overview): ~2k tokens 的结构化概览，含导航指引，用于 Rerank 和规划决策。
/// </summary>
public interface IContextLayerGenerator
{
    /// <summary>为单个文件内容生成 L0 摘要。</summary>
    Task<string> GenerateAbstractAsync(
        string content,
        string fileName,
        CancellationToken ct = default);

    /// <summary>为单个文件内容生成 L1 概览。</summary>
    Task<string> GenerateOverviewAsync(
        string content,
        string fileName,
        CancellationToken ct = default);

    /// <summary>
    /// 为目录生成 L0/L1，基于子条目的 L0 摘要聚合。
    /// 自底向上调用：叶节点先生成，父目录聚合子摘要。
    /// </summary>
    Task<(string Abstract, string Overview)> GenerateDirectoryLayersAsync(
        string directoryName,
        IReadOnlyList<string> childAbstracts,
        CancellationToken ct = default);
}
