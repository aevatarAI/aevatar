using Aevatar.Configuration;
using Aevatar.Context.Abstractions;

namespace Aevatar.Context.Core;

/// <summary>
/// aevatar:// URI 到物理路径的映射器。
/// 映射规则：
///   aevatar://skills/         → ~/.aevatar/skills/
///   aevatar://resources/      → ~/.aevatar/resources/
///   aevatar://user/{id}/      → ~/.aevatar/users/{id}/
///   aevatar://agent/{id}/     → ~/.aevatar/agents/{id}/
///   aevatar://session/{id}/   → ~/.aevatar/sessions/{id}/
/// </summary>
public sealed class AevatarUriPhysicalMapper
{
    /// <summary>将 URI 映射到物理文件系统路径。</summary>
    public string ToPhysicalPath(AevatarUri uri)
    {
        var basePath = GetScopeBasePath(uri.Scope);
        return uri.Path.Length == 0
            ? basePath
            : Path.Combine(basePath, uri.Path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>尝试从物理路径反向解析 URI。</summary>
    public AevatarUri? FromPhysicalPath(string physicalPath)
    {
        var normalized = Path.GetFullPath(physicalPath);

        var mappings = new (string Scope, string BasePath)[]
        {
            (AevatarUri.Scopes.Skills, Path.GetFullPath(AevatarPaths.Skills)),
            (AevatarUri.Scopes.Resources, Path.GetFullPath(AevatarPaths.Resources)),
            (AevatarUri.Scopes.User, Path.GetFullPath(AevatarPaths.Users)),
            (AevatarUri.Scopes.Agent, Path.GetFullPath(AevatarPaths.AgentData)),
            (AevatarUri.Scopes.Session, Path.GetFullPath(AevatarPaths.Sessions)),
        };

        foreach (var (scope, basePath) in mappings)
        {
            if (!normalized.StartsWith(basePath, StringComparison.Ordinal))
                continue;

            var relativePath = normalized[basePath.Length..].TrimStart(Path.DirectorySeparatorChar);
            var uriPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            var isDir = Directory.Exists(normalized);
            return AevatarUri.Create(scope, uriPath, isDir);
        }

        return null;
    }

    private static string GetScopeBasePath(string scope) => scope switch
    {
        AevatarUri.Scopes.Skills => AevatarPaths.Skills,
        AevatarUri.Scopes.Resources => AevatarPaths.Resources,
        AevatarUri.Scopes.User => AevatarPaths.Users,
        AevatarUri.Scopes.Agent => AevatarPaths.AgentData,
        AevatarUri.Scopes.Session => AevatarPaths.Sessions,
        _ => throw new ArgumentException($"Unknown scope: '{scope}'", nameof(scope)),
    };
}
