namespace Aevatar.AI.Abstractions.LLMProviders;

public static class UserMemoryCategories
{
    public const string Preference = "preference";
    public const string Instruction = "instruction";
    public const string Context = "context";
}

public static class UserMemorySources
{
    public const string Explicit = "explicit";
    public const string Inferred = "inferred";
}

public sealed record UserMemoryEntry(
    string Id,
    string Category,
    string Content,
    string Source,
    long CreatedAt,
    long UpdatedAt);

public sealed record UserMemoryDocument(
    int Version,
    IReadOnlyList<UserMemoryEntry> Entries)
{
    public static readonly UserMemoryDocument Empty = new(1, []);
}

public interface IUserMemoryStore
{
    Task<UserMemoryDocument> GetAsync(CancellationToken ct = default);
    Task SaveAsync(UserMemoryDocument document, CancellationToken ct = default);

    /// <summary>
    /// 追加一条记忆条目，超出上限时淘汰同类别最旧条目，再全局淘汰最旧条目。
    /// </summary>
    Task<UserMemoryEntry> AddEntryAsync(string category, string content, string source, CancellationToken ct = default);

    /// <summary>
    /// 删除指定 id 的条目，返回是否找到并删除。
    /// </summary>
    Task<bool> RemoveEntryAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// 构建可直接追加到系统 prompt 的用户记忆文本块。空时返回空字符串。
    /// </summary>
    Task<string> BuildPromptSectionAsync(int maxChars = 2000, CancellationToken ct = default);
}
