using System.Collections.Concurrent;
using Aevatar.Context.Abstractions;

namespace Aevatar.Context.Core;

/// <summary>
/// 基于内存的 IContextStore 实现，用于测试。
/// </summary>
public sealed class InMemoryContextStore : IContextStore
{
    private const string AbstractFile = ".abstract.md";
    private const string OverviewFile = ".overview.md";

    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _directories = new(StringComparer.Ordinal);

    public Task<string> ReadAsync(AevatarUri uri, CancellationToken ct = default)
    {
        var key = Normalize(uri);
        return _files.TryGetValue(key, out var content)
            ? Task.FromResult(content)
            : throw new FileNotFoundException($"Context not found: {uri}");
    }

    public Task WriteAsync(AevatarUri uri, string content, CancellationToken ct = default)
    {
        var key = Normalize(uri);
        _files[key] = content;
        EnsureParentDirectories(uri);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(AevatarUri uri, bool recursive = false, CancellationToken ct = default)
    {
        var key = Normalize(uri);

        if (uri.IsDirectory)
        {
            _directories.TryRemove(key, out _);
            if (recursive)
            {
                var prefix = key + "/";
                foreach (var k in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                    _files.TryRemove(k, out _);
                foreach (var k in _directories.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                    _directories.TryRemove(k, out _);
            }
        }
        else
        {
            _files.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContextDirectoryEntry>> ListAsync(
        AevatarUri directory,
        CancellationToken ct = default)
    {
        var prefix = Normalize(directory);
        if (prefix.Length > 0)
            prefix += "/";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<ContextDirectoryEntry>();

        foreach (var key in _files.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var remainder = key[prefix.Length..];
            var slashIndex = remainder.IndexOf('/');
            if (slashIndex >= 0)
            {
                var dirName = remainder[..slashIndex];
                if (dirName.StartsWith('.') || !seen.Add("d:" + dirName))
                    continue;
                var childUri = directory.Join(dirName + "/");
                entries.Add(new ContextDirectoryEntry(childUri, dirName, true));
            }
            else
            {
                if (remainder.StartsWith('.') || !seen.Add("f:" + remainder))
                    continue;
                var childUri = directory.Join(remainder);
                entries.Add(new ContextDirectoryEntry(childUri, remainder, false));
            }
        }

        foreach (var key in _directories.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var remainder = key[prefix.Length..];
            var slashIndex = remainder.IndexOf('/');
            var dirName = slashIndex < 0 ? remainder : remainder[..slashIndex];

            if (dirName.Length == 0 || dirName.StartsWith('.') || !seen.Add("d:" + dirName))
                continue;

            var childUri = directory.Join(dirName + "/");
            entries.Add(new ContextDirectoryEntry(childUri, dirName, true));
        }

        return Task.FromResult<IReadOnlyList<ContextDirectoryEntry>>(entries);
    }

    public Task<IReadOnlyList<AevatarUri>> GlobAsync(
        string pattern,
        AevatarUri root,
        CancellationToken ct = default)
    {
        var prefix = Normalize(root);
        if (prefix.Length > 0)
            prefix += "/";

        var results = new List<AevatarUri>();
        foreach (var key in _files.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var relative = key[prefix.Length..];
            if (MatchesGlob(relative, pattern))
            {
                if (AevatarUri.TryParse($"aevatar://{key}", out var uri))
                    results.Add(uri);
            }
        }

        return Task.FromResult<IReadOnlyList<AevatarUri>>(results);
    }

    public Task<bool> ExistsAsync(AevatarUri uri, CancellationToken ct = default)
    {
        var key = Normalize(uri);
        var exists = uri.IsDirectory
            ? _directories.ContainsKey(key) ||
              _files.Keys.Any(k => k.StartsWith(key + "/", StringComparison.Ordinal))
            : _files.ContainsKey(key);
        return Task.FromResult(exists);
    }

    public Task CreateDirectoryAsync(AevatarUri uri, CancellationToken ct = default)
    {
        var key = Normalize(uri);
        _directories[key] = 0;
        EnsureParentDirectories(uri);
        return Task.CompletedTask;
    }

    public Task<string?> GetAbstractAsync(AevatarUri uri, CancellationToken ct = default)
    {
        var dirKey = uri.IsDirectory ? Normalize(uri) : Normalize(uri.Parent);
        var key = dirKey.Length > 0 ? $"{dirKey}/{AbstractFile}" : AbstractFile;
        return Task.FromResult(_files.TryGetValue(key, out var content) ? content : null);
    }

    public Task<string?> GetOverviewAsync(AevatarUri uri, CancellationToken ct = default)
    {
        var dirKey = uri.IsDirectory ? Normalize(uri) : Normalize(uri.Parent);
        var key = dirKey.Length > 0 ? $"{dirKey}/{OverviewFile}" : OverviewFile;
        return Task.FromResult(_files.TryGetValue(key, out var content) ? content : null);
    }

    private static string Normalize(AevatarUri uri) =>
        uri.Path.Length == 0 ? uri.Scope : $"{uri.Scope}/{uri.Path}";

    private void EnsureParentDirectories(AevatarUri uri)
    {
        var current = uri.Parent;
        while (current.Path.Length > 0)
        {
            _directories[Normalize(current)] = 0;
            current = current.Parent;
        }
        _directories[current.Scope] = 0;
    }

    private static bool MatchesGlob(string path, string pattern)
    {
        if (pattern == "*")
            return !path.Contains('/');
        if (pattern.StartsWith("**/*."))
        {
            var ext = pattern["**/*.".Length..];
            return path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.StartsWith("*."))
        {
            var ext = pattern["*.".Length..];
            return !path.Contains('/') && path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase);
        }
        return path.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
