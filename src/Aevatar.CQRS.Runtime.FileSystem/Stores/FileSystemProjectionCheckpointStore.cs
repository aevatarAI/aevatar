using Aevatar.CQRS.Runtime.Abstractions.Persistence;
using Aevatar.CQRS.Runtime.FileSystem.Storage;

namespace Aevatar.CQRS.Runtime.FileSystem.Stores;

internal sealed class FileSystemProjectionCheckpointStore : IProjectionCheckpointStore
{
    private readonly CqrsPathResolver _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemProjectionCheckpointStore(CqrsPathResolver paths)
    {
        _paths = paths;
    }

    public async Task<long?> GetAsync(string projectionName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(projectionName))
            return null;

        var state = await JsonFileStorage.ReadAsync<CheckpointState>(BuildPath(projectionName), ct);
        return state?.Checkpoint;
    }

    public async Task SaveAsync(string projectionName, long checkpoint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(projectionName))
            throw new ArgumentException("Projection name is required.", nameof(projectionName));

        var state = new CheckpointState
        {
            ProjectionName = projectionName,
            Checkpoint = checkpoint,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await _gate.WaitAsync(ct);
        try
        {
            await JsonFileStorage.WriteAsync(BuildPath(projectionName), state, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string BuildPath(string projectionName)
    {
        var sanitized = projectionName.Replace('/', '_').Replace('\\', '_');
        return Path.Combine(_paths.Checkpoints, $"{sanitized}.json");
    }

    private sealed class CheckpointState
    {
        public string ProjectionName { get; set; } = string.Empty;
        public long Checkpoint { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
