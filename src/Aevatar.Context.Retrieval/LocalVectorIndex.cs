using System.Collections.Concurrent;
using System.Numerics.Tensors;
using Aevatar.Context.Abstractions;

namespace Aevatar.Context.Retrieval;

/// <summary>
/// 基于内存的本地向量索引（暴力搜索）。
/// 适用于开发和小规模场景。生产环境应替换为 HNSW 或外部向量库。
/// </summary>
public sealed class LocalVectorIndex : IContextVectorIndex
{
    private readonly ConcurrentDictionary<string, VectorIndexEntry> _entries = new();

    public Task IndexAsync(VectorIndexEntry entry, CancellationToken ct = default)
    {
        _entries[entry.Uri.ToString()] = entry;
        return Task.CompletedTask;
    }

    public Task IndexBatchAsync(IReadOnlyList<VectorIndexEntry> entries, CancellationToken ct = default)
    {
        foreach (var entry in entries)
            _entries[entry.Uri.ToString()] = entry;
        return Task.CompletedTask;
    }

    public Task DeleteByPrefixAsync(AevatarUri prefix, CancellationToken ct = default)
    {
        var prefixStr = prefix.ToString();
        foreach (var key in _entries.Keys)
        {
            if (key.StartsWith(prefixStr, StringComparison.Ordinal))
                _entries.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int topK = 10,
        AevatarUri? scopeFilter = null,
        CancellationToken ct = default)
    {
        var candidates = _entries.Values.AsEnumerable();

        if (scopeFilter.HasValue)
        {
            var scope = scopeFilter.Value;
            candidates = candidates.Where(e =>
                e.Uri.Scope == scope.Scope &&
                (scope.Path.Length == 0 || e.Uri.Path.StartsWith(scope.Path, StringComparison.Ordinal)));
        }

        var results = candidates
            .Select(e => new VectorSearchResult(
                e.Uri,
                e.ContextType,
                e.IsLeaf,
                e.Abstract,
                CosineSimilarity(queryVector.Span, e.Vector.Span)))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();

        return Task.FromResult<IReadOnlyList<VectorSearchResult>>(results);
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchChildrenAsync(
        ReadOnlyMemory<float> queryVector,
        AevatarUri parentUri,
        int topK = 5,
        CancellationToken ct = default)
    {
        var parentStr = parentUri.ToString();
        var candidates = _entries.Values
            .Where(e =>
            {
                var entryParent = e.ParentUri.ToString();
                return string.Equals(entryParent, parentStr, StringComparison.Ordinal);
            });

        var results = candidates
            .Select(e => new VectorSearchResult(
                e.Uri,
                e.ContextType,
                e.IsLeaf,
                e.Abstract,
                CosineSimilarity(queryVector.Span, e.Vector.Span)))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();

        return Task.FromResult<IReadOnlyList<VectorSearchResult>>(results);
    }

    private static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0f;

        return TensorPrimitives.CosineSimilarity(a, b);
    }
}
