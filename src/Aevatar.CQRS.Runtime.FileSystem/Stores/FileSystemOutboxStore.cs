using Aevatar.CQRS.Runtime.Abstractions.Persistence;
using Aevatar.CQRS.Runtime.FileSystem.Storage;

namespace Aevatar.CQRS.Runtime.FileSystem.Stores;

internal sealed class FileSystemOutboxStore : IOutboxStore
{
    private readonly CqrsPathResolver _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemOutboxStore(CqrsPathResolver paths)
    {
        _paths = paths;
    }

    public async Task AppendAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(message.MessageId))
            message.MessageId = Guid.NewGuid().ToString("N");
        if (message.CreatedAt == default)
            message.CreatedAt = DateTimeOffset.UtcNow;

        var path = BuildPath(message.MessageId);
        await _gate.WaitAsync(ct);
        try
        {
            await JsonFileStorage.WriteAsync(path, message, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OutboxMessage>> ListPendingAsync(int take = 100, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var boundedTake = Math.Clamp(take, 1, 1000);
        var files = Directory
            .EnumerateFiles(_paths.Outbox, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(File.GetCreationTimeUtc)
            .Take(boundedTake)
            .ToList();

        var items = new List<OutboxMessage>(files.Count);
        foreach (var file in files)
        {
            var message = await JsonFileStorage.ReadAsync<OutboxMessage>(file, ct);
            if (message is { Dispatched: false })
                items.Add(message);
        }

        return items;
    }

    public async Task MarkDispatchedAsync(string messageId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(messageId))
            return;

        var path = BuildPath(messageId);
        await _gate.WaitAsync(ct);
        try
        {
            var message = await JsonFileStorage.ReadAsync<OutboxMessage>(path, ct);
            if (message == null)
                return;

            message.Dispatched = true;
            message.DispatchedAt = DateTimeOffset.UtcNow;
            await JsonFileStorage.WriteAsync(path, message, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string BuildPath(string messageId) =>
        Path.Combine(_paths.Outbox, $"{messageId}.json");
}
