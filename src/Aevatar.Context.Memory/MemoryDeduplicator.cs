using Aevatar.Context.Abstractions;
using Aevatar.Context.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Context.Memory;

/// <summary>
/// 记忆去重决策器。
/// 向量预过滤找到相似记忆 → 决定 CREATE / UPDATE / MERGE / SKIP。
/// </summary>
public sealed class MemoryDeduplicator
{
    private readonly IContextVectorIndex _vectorIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ILogger _logger;

    private const float SimilarityThreshold = 0.85f;

    public MemoryDeduplicator(
        IContextVectorIndex vectorIndex,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ILogger<MemoryDeduplicator>? logger = null)
    {
        _vectorIndex = vectorIndex;
        _embedder = embedder;
        _logger = logger ?? NullLogger<MemoryDeduplicator>.Instance;
    }

    /// <summary>
    /// 对候选记忆执行去重决策。
    /// 返回需要实际写入的记忆及其决策。
    /// </summary>
    public async Task<IReadOnlyList<DeduplicationResult>> DeduplicateAsync(
        IReadOnlyList<CandidateMemory> candidates,
        string userId,
        string agentId,
        CancellationToken ct = default)
    {
        var results = new List<DeduplicationResult>();

        foreach (var candidate in candidates)
        {
            var targetUri = ResolveTargetUri(candidate.Category, userId, agentId);
            var embedding = await _embedder.GenerateAsync(candidate.Content, cancellationToken: ct);

            var similar = await _vectorIndex.SearchAsync(
                embedding.Vector,
                topK: 3,
                scopeFilter: targetUri,
                ct: ct);

            var bestMatch = similar.FirstOrDefault(r => r.Score >= SimilarityThreshold);

            DeduplicationDecision decision;
            AevatarUri? existingUri = null;

            if (bestMatch == null)
            {
                decision = DeduplicationDecision.Create;
            }
            else if (IsMergeable(candidate.Category))
            {
                decision = DeduplicationDecision.Update;
                existingUri = bestMatch.Uri;
            }
            else if (bestMatch.Score > 0.95f)
            {
                decision = DeduplicationDecision.Skip;
                existingUri = bestMatch.Uri;
            }
            else
            {
                decision = DeduplicationDecision.Create;
            }

            results.Add(new DeduplicationResult(candidate, decision, targetUri, existingUri));
            _logger.LogDebug("Dedup: {Category} → {Decision} (best match: {Score:F3})",
                candidate.Category, decision, bestMatch?.Score ?? 0f);
        }

        return results;
    }

    private static AevatarUri ResolveTargetUri(
        MemoryCategory category,
        string userId,
        string agentId) => category switch
    {
        MemoryCategory.Profile =>
            AevatarUri.Create(AevatarUri.Scopes.User, $"{userId}/memories"),
        MemoryCategory.Preferences =>
            AevatarUri.Create(AevatarUri.Scopes.User, $"{userId}/memories/preferences"),
        MemoryCategory.Entities =>
            AevatarUri.Create(AevatarUri.Scopes.User, $"{userId}/memories/entities"),
        MemoryCategory.Events =>
            AevatarUri.Create(AevatarUri.Scopes.User, $"{userId}/memories/events"),
        MemoryCategory.Cases =>
            AevatarUri.Create(AevatarUri.Scopes.Agent, $"{agentId}/memories/cases"),
        MemoryCategory.Patterns =>
            AevatarUri.Create(AevatarUri.Scopes.Agent, $"{agentId}/memories/patterns"),
        _ => AevatarUri.Create(AevatarUri.Scopes.User, $"{userId}/memories"),
    };

    private static bool IsMergeable(MemoryCategory category) => category switch
    {
        MemoryCategory.Profile => true,
        MemoryCategory.Preferences => true,
        MemoryCategory.Entities => true,
        MemoryCategory.Patterns => true,
        _ => false, // Events and Cases are immutable
    };
}

/// <summary>去重决策。</summary>
public enum DeduplicationDecision
{
    /// <summary>新记忆，直接创建。</summary>
    Create,

    /// <summary>更新已有记忆。</summary>
    Update,

    /// <summary>合并到已有记忆。</summary>
    Merge,

    /// <summary>重复，跳过。</summary>
    Skip,
}

/// <summary>去重决策结果。</summary>
public sealed record DeduplicationResult(
    CandidateMemory Candidate,
    DeduplicationDecision Decision,
    AevatarUri TargetScope,
    AevatarUri? ExistingUri);
