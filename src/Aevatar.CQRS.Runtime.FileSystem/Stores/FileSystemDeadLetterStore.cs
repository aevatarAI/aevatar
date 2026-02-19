using Aevatar.CQRS.Runtime.Abstractions.Persistence;
using Aevatar.CQRS.Runtime.FileSystem.Storage;

namespace Aevatar.CQRS.Runtime.FileSystem.Stores;

internal sealed class FileSystemDeadLetterStore : IDeadLetterStore
{
    private readonly CqrsPathResolver _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemDeadLetterStore(CqrsPathResolver paths)
    {
        _paths = paths;
    }

    public async Task AppendAsync(DeadLetterMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(message.DeadLetterId))
            message.DeadLetterId = Guid.NewGuid().ToString("N");
        if (message.CreatedAt == default)
            message.CreatedAt = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(ct);
        try
        {
            await JsonFileStorage.WriteAsync(BuildPath(message.DeadLetterId), message, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DeadLetterMessage>> ListAsync(int take = 100, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var boundedTake = Math.Clamp(take, 1, 1000);
        var files = Directory
            .EnumerateFiles(_paths.DeadLetters, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetCreationTimeUtc)
            .Take(boundedTake)
            .ToList();

        var items = new List<DeadLetterMessage>(files.Count);
        foreach (var file in files)
        {
            var message = await JsonFileStorage.ReadAsync<DeadLetterMessage>(file, ct);
            if (message != null)
                items.Add(message);
        }

        return items;
    }

    private string BuildPath(string deadLetterId)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_paths.DeadLetters, $"{deadLetterId}.json"));
        var normalizedBase = Path.GetFullPath(_paths.DeadLetters + Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Identifier contains invalid path characters.", nameof(deadLetterId));
        return fullPath;
    }
}
