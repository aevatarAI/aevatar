namespace Aevatar.Context.Abstractions;

/// <summary>
/// 上下文语义检索抽象。
/// Phase 2 实现；Phase 1 仅定义接口。
/// </summary>
public interface IContextRetriever
{
    /// <summary>
    /// 简单语义搜索（无 session 上下文）。
    /// </summary>
    Task<FindResult> FindAsync(
        string query,
        AevatarUri? targetScope = null,
        CancellationToken ct = default);

    /// <summary>
    /// 复杂语义搜索（有 session 上下文，走意图分析）。
    /// </summary>
    Task<FindResult> SearchAsync(
        string query,
        SessionInfo session,
        CancellationToken ct = default);
}

/// <summary>检索结果，按类型分组。</summary>
public sealed record FindResult(
    IReadOnlyList<MatchedContext> Memories,
    IReadOnlyList<MatchedContext> Resources,
    IReadOnlyList<MatchedContext> Skills)
{
    public static FindResult Empty { get; } = new([], [], []);

    public int Total => Memories.Count + Resources.Count + Skills.Count;
}

/// <summary>单个匹配的上下文条目。</summary>
public sealed record MatchedContext(
    AevatarUri Uri,
    ContextType Type,
    bool IsDirectory,
    string Abstract,
    float Score);

/// <summary>Session 信息，用于复杂检索的意图分析。</summary>
public sealed record SessionInfo(
    string SessionId,
    IReadOnlyList<string> RecentMessages);
