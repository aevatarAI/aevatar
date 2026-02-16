namespace Aevatar.Context.Abstractions;

/// <summary>
/// 上下文存储抽象——虚拟文件系统的 CRUD 操作。
/// </summary>
public interface IContextStore
{
    // ─── 文件系统操作 ───

    /// <summary>读取文件内容。</summary>
    Task<string> ReadAsync(AevatarUri uri, CancellationToken ct = default);

    /// <summary>写入文件内容（自动创建父目录）。</summary>
    Task WriteAsync(AevatarUri uri, string content, CancellationToken ct = default);

    /// <summary>删除文件或目录。</summary>
    Task DeleteAsync(AevatarUri uri, bool recursive = false, CancellationToken ct = default);

    /// <summary>列举目录的直接子条目。</summary>
    Task<IReadOnlyList<ContextDirectoryEntry>> ListAsync(AevatarUri directory, CancellationToken ct = default);

    /// <summary>按 glob 模式搜索文件。</summary>
    Task<IReadOnlyList<AevatarUri>> GlobAsync(string pattern, AevatarUri root, CancellationToken ct = default);

    /// <summary>检查文件或目录是否存在。</summary>
    Task<bool> ExistsAsync(AevatarUri uri, CancellationToken ct = default);

    /// <summary>创建目录。</summary>
    Task CreateDirectoryAsync(AevatarUri uri, CancellationToken ct = default);

    // ─── L0/L1 快捷访问 ───

    /// <summary>读取 L0 摘要（.abstract.md）。</summary>
    Task<string?> GetAbstractAsync(AevatarUri uri, CancellationToken ct = default);

    /// <summary>读取 L1 概览（.overview.md）。</summary>
    Task<string?> GetOverviewAsync(AevatarUri uri, CancellationToken ct = default);
}
