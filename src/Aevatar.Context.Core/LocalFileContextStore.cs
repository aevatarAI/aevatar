using Aevatar.Context.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Context.Core;

/// <summary>
/// 基于本地文件系统的 IContextStore 实现。
/// 通过 AevatarUriPhysicalMapper 将 aevatar:// URI 映射到 ~/.aevatar/ 下的物理路径。
/// </summary>
public sealed class LocalFileContextStore : IContextStore
{
    private const string AbstractFile = ".abstract.md";
    private const string OverviewFile = ".overview.md";

    private readonly AevatarUriPhysicalMapper _mapper;
    private readonly ILogger _logger;

    public LocalFileContextStore(
        AevatarUriPhysicalMapper mapper,
        ILogger<LocalFileContextStore>? logger = null)
    {
        _mapper = mapper;
        _logger = logger ?? NullLogger<LocalFileContextStore>.Instance;
    }

    public async Task<string> ReadAsync(AevatarUri uri, CancellationToken ct = default)
    {
        var path = _mapper.ToPhysicalPath(uri);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Context file not found: {uri}", path);

        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task WriteAsync(AevatarUri uri, string content, CancellationToken ct = default)
    {
        var path = _mapper.ToPhysicalPath(uri);
        var dir = Path.GetDirectoryName(path);
        if (dir != null)
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content, ct);
        _logger.LogDebug("Wrote context: {Uri} → {Path}", uri, path);
    }

    public Task DeleteAsync(AevatarUri uri, bool recursive = false, CancellationToken ct = default)
    {
        var path = _mapper.ToPhysicalPath(uri);

        if (uri.IsDirectory && Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
            _logger.LogDebug("Deleted directory: {Uri}", uri);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogDebug("Deleted file: {Uri}", uri);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContextDirectoryEntry>> ListAsync(
        AevatarUri directory,
        CancellationToken ct = default)
    {
        var path = _mapper.ToPhysicalPath(directory);
        if (!Directory.Exists(path))
            return Task.FromResult<IReadOnlyList<ContextDirectoryEntry>>([]);

        var entries = new List<ContextDirectoryEntry>();

        foreach (var dir in Directory.GetDirectories(path))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.'))
                continue;

            var childUri = directory.Join(name + "/");
            var modified = Directory.GetLastWriteTimeUtc(dir);
            entries.Add(new ContextDirectoryEntry(childUri, name, true, modified));
        }

        foreach (var file in Directory.GetFiles(path))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('.'))
                continue;

            var childUri = directory.Join(name);
            var modified = File.GetLastWriteTimeUtc(file);
            entries.Add(new ContextDirectoryEntry(childUri, name, false, modified));
        }

        return Task.FromResult<IReadOnlyList<ContextDirectoryEntry>>(entries);
    }

    public Task<IReadOnlyList<AevatarUri>> GlobAsync(
        string pattern,
        AevatarUri root,
        CancellationToken ct = default)
    {
        var basePath = _mapper.ToPhysicalPath(root);
        if (!Directory.Exists(basePath))
            return Task.FromResult<IReadOnlyList<AevatarUri>>([]);

        var results = new List<AevatarUri>();
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var searchPattern = pattern.Replace("**/", "").Replace("**\\", "").Replace("**", "*");
        foreach (var file in Directory.GetFiles(basePath, searchPattern, enumOptions))
        {
            var uri = _mapper.FromPhysicalPath(file);
            if (uri.HasValue)
                results.Add(uri.Value);
        }

        return Task.FromResult<IReadOnlyList<AevatarUri>>(results);
    }

    public Task<bool> ExistsAsync(AevatarUri uri, CancellationToken ct = default)
    {
        var path = _mapper.ToPhysicalPath(uri);
        var exists = uri.IsDirectory ? Directory.Exists(path) : File.Exists(path);
        return Task.FromResult(exists);
    }

    public Task CreateDirectoryAsync(AevatarUri uri, CancellationToken ct = default)
    {
        var path = _mapper.ToPhysicalPath(uri);
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public async Task<string?> GetAbstractAsync(AevatarUri uri, CancellationToken ct = default)
    {
        var dirUri = uri.IsDirectory ? uri : uri.Parent;
        var abstractUri = dirUri.Join(AbstractFile);
        var path = _mapper.ToPhysicalPath(abstractUri);
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, ct)
            : null;
    }

    public async Task<string?> GetOverviewAsync(AevatarUri uri, CancellationToken ct = default)
    {
        var dirUri = uri.IsDirectory ? uri : uri.Parent;
        var overviewUri = dirUri.Join(OverviewFile);
        var path = _mapper.ToPhysicalPath(overviewUri);
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, ct)
            : null;
    }
}
