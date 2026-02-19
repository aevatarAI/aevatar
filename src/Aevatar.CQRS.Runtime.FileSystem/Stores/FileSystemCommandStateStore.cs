using Aevatar.CQRS.Runtime.Abstractions.Commands;
using Aevatar.CQRS.Runtime.FileSystem.Storage;

namespace Aevatar.CQRS.Runtime.FileSystem.Stores;

internal sealed class FileSystemCommandStateStore : ICommandStateStore
{
    private readonly CqrsPathResolver _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemCommandStateStore(CqrsPathResolver paths)
    {
        _paths = paths;
    }

    public async Task UpsertAsync(CommandExecutionState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ct.ThrowIfCancellationRequested();

        var path = BuildPath(state.CommandId);
        await _gate.WaitAsync(ct);
        try
        {
            await JsonFileStorage.WriteAsync(path, state, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommandExecutionState?> GetAsync(string commandId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(commandId))
            return null;

        return await JsonFileStorage.ReadAsync<CommandExecutionState>(BuildPath(commandId), ct);
    }

    public async Task<IReadOnlyList<CommandExecutionState>> ListAsync(int take = 100, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var boundedTake = Math.Clamp(take, 1, 1000);
        var files = Directory
            .EnumerateFiles(_paths.Commands, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(boundedTake)
            .ToList();

        var items = new List<CommandExecutionState>(files.Count);
        foreach (var file in files)
        {
            var state = await JsonFileStorage.ReadAsync<CommandExecutionState>(file, ct);
            if (state != null)
                items.Add(state);
        }

        return items;
    }

    private string BuildPath(string commandId)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_paths.Commands, $"{commandId}.json"));
        var normalizedBase = Path.GetFullPath(_paths.Commands + Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Identifier contains invalid path characters.", nameof(commandId));
        return fullPath;
    }
}
