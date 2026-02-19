using System.Text.Json;
using Aevatar.CQRS.Sagas.Runtime.FileSystem.Storage;

namespace Aevatar.CQRS.Sagas.Runtime.FileSystem.Timeouts;

internal sealed class FileSystemSagaTimeoutStore
{
    private readonly SagaPathResolver _paths;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public FileSystemSagaTimeoutStore(SagaPathResolver paths)
    {
        _paths = paths;
    }

    public async Task EnqueueAsync(SagaTimeoutScheduleRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(record.TimeoutId))
            record.TimeoutId = Guid.NewGuid().ToString("N");

        var path = BuildPendingPath(record.DueAt, record.TimeoutId);
        await JsonFileStorage.WriteAsync(path, record, _jsonOptions, ct);
    }

    public IReadOnlyList<string> ListPendingPaths(int take = 100)
    {
        var boundedTake = Math.Clamp(take, 1, 1000);
        return Directory
            .EnumerateFiles(_paths.TimeoutPending, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Take(boundedTake)
            .ToList();
    }

    public bool IsDueByFileName(string path, DateTimeOffset now)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var split = fileName.Split('_', 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length < 2 || !long.TryParse(split[0], out var dueMs))
            return true;

        var dueAt = DateTimeOffset.FromUnixTimeMilliseconds(dueMs);
        return dueAt <= now;
    }

    public bool TryClaim(string pendingPath, out string processingPath)
    {
        processingPath = BuildProcessingPath(pendingPath);
        try
        {
            File.Move(pendingPath, processingPath, overwrite: false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Task<SagaTimeoutScheduleRecord?> ReadAsync(string path, CancellationToken ct = default) =>
        JsonFileStorage.ReadAsync<SagaTimeoutScheduleRecord>(path, _jsonOptions, ct);

    public Task CompleteAsync(string processingPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(processingPath))
            File.Delete(processingPath);
        return Task.CompletedTask;
    }

    public async Task RequeueAsync(
        string processingPath,
        SagaTimeoutScheduleRecord record,
        DateTimeOffset dueAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        record.DueAt = dueAt;
        if (File.Exists(processingPath))
            File.Delete(processingPath);

        await EnqueueAsync(record, ct);
    }

    private string BuildPendingPath(DateTimeOffset dueAt, string timeoutId)
    {
        var dueMs = dueAt.ToUnixTimeMilliseconds();
        return Path.Combine(_paths.TimeoutPending, $"{dueMs:D20}_{timeoutId}.json");
    }

    private string BuildProcessingPath(string pendingPath)
    {
        var fileName = Path.GetFileName(pendingPath);
        return Path.Combine(_paths.TimeoutProcessing, fileName);
    }
}
