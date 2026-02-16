using Aevatar.Context.Abstractions;

namespace Aevatar.Context.Retrieval;

/// <summary>
/// 上下文向量索引抽象。
/// 存储 URI + 向量 + 元数据，支持语义搜索。
/// 不存储文件内容（内容通过 IContextStore 读取）。
/// </summary>
public interface IContextVectorIndex
{
    /// <summary>索引一个上下文条目。</summary>
    Task IndexAsync(VectorIndexEntry entry, CancellationToken ct = default);

    /// <summary>批量索引。</summary>
    Task IndexBatchAsync(IReadOnlyList<VectorIndexEntry> entries, CancellationToken ct = default);

    /// <summary>按 URI 前缀删除索引。</summary>
    Task DeleteByPrefixAsync(AevatarUri prefix, CancellationToken ct = default);

    /// <summary>
    /// 向量搜索。返回按相似度降序排列的结果。
    /// </summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int topK = 10,
        AevatarUri? scopeFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// 按父 URI 搜索子条目。
    /// </summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchChildrenAsync(
        ReadOnlyMemory<float> queryVector,
        AevatarUri parentUri,
        int topK = 5,
        CancellationToken ct = default);
}

/// <summary>写入向量索引的条目。</summary>
public sealed record VectorIndexEntry(
    AevatarUri Uri,
    AevatarUri ParentUri,
    ContextType ContextType,
    bool IsLeaf,
    ReadOnlyMemory<float> Vector,
    string Abstract,
    string Name);

/// <summary>向量搜索结果。</summary>
public sealed record VectorSearchResult(
    AevatarUri Uri,
    ContextType ContextType,
    bool IsLeaf,
    string Abstract,
    float Score);
