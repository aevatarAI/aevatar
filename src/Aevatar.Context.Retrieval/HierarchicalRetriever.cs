using Aevatar.Context.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Context.Retrieval;

/// <summary>
/// 层级递归检索器。
/// 使用优先级队列驱动目录级递归下钻：
/// 1. 全局向量搜索定位高分目录
/// 2. 目录内二次检索精细探索
/// 3. 递归下钻子目录
/// 4. 收敛检测（TopK 3轮不变则停止）
/// </summary>
public sealed class HierarchicalRetriever : IContextRetriever
{
    private const float ScorePropagationAlpha = 0.5f;
    private const int MaxConvergenceRounds = 3;
    private const int GlobalSearchTopK = 3;

    private readonly IContextVectorIndex _vectorIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly IntentAnalyzer _intentAnalyzer;
    private readonly ILogger _logger;

    public HierarchicalRetriever(
        IContextVectorIndex vectorIndex,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IntentAnalyzer intentAnalyzer,
        ILogger<HierarchicalRetriever>? logger = null)
    {
        _vectorIndex = vectorIndex;
        _embedder = embedder;
        _intentAnalyzer = intentAnalyzer;
        _logger = logger ?? NullLogger<HierarchicalRetriever>.Instance;
    }

    public async Task<FindResult> FindAsync(
        string query,
        AevatarUri? targetScope = null,
        CancellationToken ct = default)
    {
        var embedding = await _embedder.GenerateAsync(query, cancellationToken: ct);
        var queryVector = embedding.Vector;

        var results = await _vectorIndex.SearchAsync(
            queryVector, topK: 10, scopeFilter: targetScope, ct: ct);

        return CategorizeResults(results);
    }

    public async Task<FindResult> SearchAsync(
        string query,
        SessionInfo session,
        CancellationToken ct = default)
    {
        var typedQueries = await _intentAnalyzer.AnalyzeAsync(query, session, ct);
        if (typedQueries.Count == 0)
            return FindResult.Empty;

        var allResults = new List<VectorSearchResult>();

        foreach (var tq in typedQueries)
        {
            var results = await ExecuteHierarchicalSearchAsync(tq, ct);
            allResults.AddRange(results);
        }

        var deduplicated = allResults
            .GroupBy(r => r.Uri)
            .Select(g => g.OrderByDescending(r => r.Score).First())
            .OrderByDescending(r => r.Score)
            .ToList();

        return CategorizeResults(deduplicated);
    }

    private async Task<IReadOnlyList<VectorSearchResult>> ExecuteHierarchicalSearchAsync(
        TypedQuery typedQuery,
        CancellationToken ct)
    {
        var embedding = await _embedder.GenerateAsync(typedQuery.Query, cancellationToken: ct);
        var queryVector = embedding.Vector;

        var scopeUri = GetRootUri(typedQuery.ContextType);

        // Step 1: global search to locate starting directories
        var globalResults = await _vectorIndex.SearchAsync(
            queryVector, topK: GlobalSearchTopK, scopeFilter: scopeUri, ct: ct);

        // Step 2: hierarchical drill-down via priority queue
        var collected = new List<VectorSearchResult>();
        var dirQueue = new PriorityQueue<(AevatarUri Uri, float Score), float>();
        var visited = new HashSet<AevatarUri>();

        foreach (var result in globalResults)
        {
            collected.Add(result);

            if (!result.IsLeaf)
            {
                dirQueue.Enqueue((result.Uri, result.Score), -result.Score);
            }
        }

        var unchangedRounds = 0;
        var previousTopScore = collected.Count > 0 ? collected.Max(r => r.Score) : 0f;

        while (dirQueue.Count > 0 && unchangedRounds < MaxConvergenceRounds)
        {
            var (dirUri, parentScore) = dirQueue.Dequeue();

            if (!visited.Add(dirUri))
                continue;

            // Search children of this directory
            var childResults = await _vectorIndex.SearchChildrenAsync(
                queryVector, dirUri, topK: 5, ct: ct);

            foreach (var child in childResults)
            {
                var finalScore = ScorePropagationAlpha * child.Score +
                                 (1 - ScorePropagationAlpha) * parentScore;

                var propagated = child with { Score = finalScore };
                collected.Add(propagated);

                if (!child.IsLeaf && !visited.Contains(child.Uri))
                {
                    dirQueue.Enqueue((child.Uri, finalScore), -finalScore);
                }
            }

            // Convergence detection
            var currentTopScore = collected.Count > 0 ? collected.Max(r => r.Score) : 0f;
            if (Math.Abs(currentTopScore - previousTopScore) < 0.001f)
                unchangedRounds++;
            else
                unchangedRounds = 0;

            previousTopScore = currentTopScore;
        }

        _logger.LogDebug(
            "Hierarchical search for '{Query}' ({Type}): {Count} results, {Visited} dirs visited",
            typedQuery.Query, typedQuery.ContextType, collected.Count, visited.Count);

        return collected
            .GroupBy(r => r.Uri)
            .Select(g => g.OrderByDescending(r => r.Score).First())
            .OrderByDescending(r => r.Score)
            .Take(10)
            .ToList();
    }

    private static AevatarUri? GetRootUri(ContextType type) => type switch
    {
        ContextType.Skill => AevatarUri.SkillsRoot(),
        ContextType.Resource => AevatarUri.ResourcesRoot(),
        ContextType.Memory => null, // Search across both user and agent memories
        _ => null,
    };

    private static FindResult CategorizeResults(IReadOnlyList<VectorSearchResult> results)
    {
        var memories = new List<MatchedContext>();
        var resources = new List<MatchedContext>();
        var skills = new List<MatchedContext>();

        foreach (var r in results)
        {
            var matched = new MatchedContext(r.Uri, r.ContextType, r.IsLeaf, r.Abstract, r.Score);

            switch (r.ContextType)
            {
                case ContextType.Memory:
                    memories.Add(matched);
                    break;
                case ContextType.Skill:
                    skills.Add(matched);
                    break;
                default:
                    resources.Add(matched);
                    break;
            }
        }

        return new FindResult(memories, resources, skills);
    }
}
