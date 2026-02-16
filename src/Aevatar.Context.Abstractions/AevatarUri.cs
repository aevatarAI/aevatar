using System.Diagnostics.CodeAnalysis;

namespace Aevatar.Context.Abstractions;

/// <summary>
/// aevatar:// 虚拟文件系统 URI 值对象。
/// 格式: aevatar://{scope}/{path}
/// </summary>
/// <example>
/// aevatar://skills/web-search/SKILL.md
/// aevatar://resources/my-project/docs/
/// aevatar://user/u123/memories/preferences/
/// aevatar://agent/a456/memories/cases/
/// aevatar://session/run789/messages.jsonl
/// </example>
public readonly record struct AevatarUri : IComparable<AevatarUri>
{
    public const string SchemeName = "aevatar";
    private const string SchemePrefix = "aevatar://";

    /// <summary>顶级作用域名称。</summary>
    public string Scope { get; }

    /// <summary>作用域内的路径（不含前导 /）。</summary>
    public string Path { get; }

    /// <summary>是否为目录（路径以 / 结尾或路径为空）。</summary>
    public bool IsDirectory { get; }

    private AevatarUri(string scope, string path, bool isDirectory)
    {
        Scope = scope;
        Path = path;
        IsDirectory = isDirectory;
    }

    // ─── 已知 Scope 常量 ───

    public static class Scopes
    {
        public const string Skills = "skills";
        public const string Resources = "resources";
        public const string User = "user";
        public const string Agent = "agent";
        public const string Session = "session";
    }

    // ─── 工厂方法 ───

    /// <summary>解析 URI 字符串。</summary>
    public static AevatarUri Parse(string uri)
    {
        if (!TryParse(uri, out var result))
            throw new FormatException($"Invalid aevatar URI: '{uri}'");
        return result;
    }

    /// <summary>尝试解析 URI 字符串。</summary>
    public static bool TryParse(string? uri, [NotNullWhen(true)] out AevatarUri result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        if (!uri.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var body = uri[SchemePrefix.Length..];
        if (body.Length == 0)
            return false;

        var slashIndex = body.IndexOf('/');
        string scope;
        string path;
        bool isDirectory;

        if (slashIndex < 0)
        {
            scope = body;
            path = "";
            isDirectory = true;
        }
        else
        {
            scope = body[..slashIndex];
            path = body[(slashIndex + 1)..];
            isDirectory = path.Length == 0 || path.EndsWith('/');
        }

        if (scope.Length == 0)
            return false;

        // 规范化：去除 path 末尾的 /（IsDirectory 已经记录）
        path = path.TrimEnd('/');

        result = new AevatarUri(scope.ToLowerInvariant(), path, isDirectory);
        return true;
    }

    /// <summary>从 scope 和 path 段构建。</summary>
    public static AevatarUri Create(string scope, string path = "", bool isDirectory = true) =>
        new(scope.ToLowerInvariant(), path.Trim('/'), isDirectory);

    // ─── 快捷构建 ───

    public static AevatarUri SkillsRoot() =>
        new(Scopes.Skills, "", true);

    public static AevatarUri ResourcesRoot() =>
        new(Scopes.Resources, "", true);

    public static AevatarUri UserRoot(string userId) =>
        new(Scopes.User, userId, true);

    public static AevatarUri AgentRoot(string agentId) =>
        new(Scopes.Agent, agentId, true);

    public static AevatarUri SessionRoot(string runId) =>
        new(Scopes.Session, runId, true);

    // ─── 导航 ───

    /// <summary>获取父 URI。根级别返回自身。</summary>
    public AevatarUri Parent
    {
        get
        {
            if (Path.Length == 0)
                return this;

            var lastSlash = Path.LastIndexOf('/');
            var parentPath = lastSlash < 0 ? "" : Path[..lastSlash];
            return new AevatarUri(Scope, parentPath, true);
        }
    }

    /// <summary>拼接子路径。</summary>
    public AevatarUri Join(string segment)
    {
        var trimmed = segment.Trim('/');
        if (trimmed.Length == 0)
            return this;

        var newPath = Path.Length == 0 ? trimmed : $"{Path}/{trimmed}";
        var isDir = segment.EndsWith('/');
        return new AevatarUri(Scope, newPath, isDir);
    }

    /// <summary>获取最后一段名称。</summary>
    public string Name
    {
        get
        {
            if (Path.Length == 0)
                return Scope;

            var lastSlash = Path.LastIndexOf('/');
            return lastSlash < 0 ? Path : Path[(lastSlash + 1)..];
        }
    }

    /// <summary>判断 other 是否是当前 URI 的后代。</summary>
    public bool IsAncestorOf(AevatarUri other)
    {
        if (!string.Equals(Scope, other.Scope, StringComparison.Ordinal))
            return false;

        if (Path.Length == 0)
            return other.Path.Length > 0;

        return other.Path.StartsWith(Path + "/", StringComparison.Ordinal);
    }

    // ─── 格式化 ───

    public override string ToString()
    {
        if (Path.Length == 0)
            return $"{SchemePrefix}{Scope}/";

        return IsDirectory
            ? $"{SchemePrefix}{Scope}/{Path}/"
            : $"{SchemePrefix}{Scope}/{Path}";
    }

    public int CompareTo(AevatarUri other)
    {
        var scopeCompare = string.Compare(Scope, other.Scope, StringComparison.Ordinal);
        return scopeCompare != 0
            ? scopeCompare
            : string.Compare(Path, other.Path, StringComparison.Ordinal);
    }
}
