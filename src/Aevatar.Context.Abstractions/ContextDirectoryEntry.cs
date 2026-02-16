namespace Aevatar.Context.Abstractions;

/// <summary>
/// 目录列举结果中的单个条目。
/// </summary>
public sealed record ContextDirectoryEntry(
    AevatarUri Uri,
    string Name,
    bool IsDirectory,
    DateTimeOffset? LastModified = null);
