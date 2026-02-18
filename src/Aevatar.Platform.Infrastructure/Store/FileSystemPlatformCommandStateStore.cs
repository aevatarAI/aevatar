using Aevatar.CQRS.Runtime.Abstractions.Configuration;
using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Application.Abstractions.Ports;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Aevatar.Platform.Infrastructure.Store;

internal sealed class FileSystemPlatformCommandStateStore : IPlatformCommandStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _statesPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemPlatformCommandStateStore(IOptions<CqrsRuntimeOptions> options)
    {
        var root = options.Value.WorkingDirectory;
        if (!Path.IsPathRooted(root))
            root = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), root));

        _statesPath = Path.Combine(root, "platform-command-states");
        Directory.CreateDirectory(_statesPath);
    }

    public async Task UpsertAsync(PlatformCommandStatus status, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ct.ThrowIfCancellationRequested();

        var path = BuildPath(status.CommandId);
        await _gate.WaitAsync(ct);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                useAsync: true);
            await JsonSerializer.SerializeAsync(stream, status, JsonOptions, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlatformCommandStatus?> GetAsync(string commandId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(commandId))
            return null;

        var path = BuildPath(commandId);
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync<PlatformCommandStatus>(stream, JsonOptions, ct);
    }

    public async Task<IReadOnlyList<PlatformCommandStatus>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var boundedTake = Math.Clamp(take, 1, 500);
        var files = Directory
            .EnumerateFiles(_statesPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(boundedTake)
            .ToList();

        var items = new List<PlatformCommandStatus>(files.Count);
        foreach (var file in files)
        {
            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                useAsync: true);
            var status = await JsonSerializer.DeserializeAsync<PlatformCommandStatus>(stream, JsonOptions, ct);
            if (status != null)
                items.Add(status);
        }

        return items;
    }

    private string BuildPath(string commandId) => Path.Combine(_statesPath, $"{commandId}.json");
}
