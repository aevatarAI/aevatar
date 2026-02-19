using Aevatar.CQRS.Runtime.Abstractions.Persistence;
using Aevatar.CQRS.Runtime.FileSystem.Storage;

namespace Aevatar.CQRS.Runtime.FileSystem.Stores;

internal sealed class FileSystemInboxStore : IInboxStore
{
    private readonly CqrsPathResolver _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemInboxStore(CqrsPathResolver paths)
    {
        _paths = paths;
    }

    public async Task<bool> TryAcquireAsync(string commandId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(commandId))
            return false;

        var path = BuildPath(commandId);
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
                return false;

            await JsonFileStorage.WriteAsync(path, new InboxState
            {
                CommandId = commandId,
                Status = "acquired",
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkCompletedAsync(string commandId, CancellationToken ct = default) =>
        MarkStatusAsync(commandId, "completed", string.Empty, ct);

    public Task MarkFailedAsync(string commandId, string error, CancellationToken ct = default) =>
        MarkStatusAsync(commandId, "failed", error, ct);

    private async Task MarkStatusAsync(
        string commandId,
        string status,
        string error,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(commandId))
            return;

        var path = BuildPath(commandId);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await JsonFileStorage.ReadAsync<InboxState>(path, ct) ?? new InboxState
            {
                CommandId = commandId,
            };

            current.Status = status;
            current.Error = error;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            await JsonFileStorage.WriteAsync(path, current, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string BuildPath(string commandId)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_paths.Inbox, $"{commandId}.json"));
        var normalizedBase = Path.GetFullPath(_paths.Inbox + Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Identifier contains invalid path characters.", nameof(commandId));
        return fullPath;
    }

    private sealed class InboxState
    {
        public string CommandId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
