using System.Collections.Concurrent;
using System.Text;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Persistence;

/// <summary>
/// File-backed event store.
/// Stores each agent stream in a local file with optimistic concurrency checks.
/// </summary>
public sealed class FileEventStore : IEventStore
{
    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentLocks = new(StringComparer.Ordinal);
    private readonly ILogger<FileEventStore> _logger;

    public FileEventStore(
        FileEventStoreOptions? options = null,
        ILogger<FileEventStore>? logger = null)
    {
        options ??= new FileEventStoreOptions();
        if (string.IsNullOrWhiteSpace(options.RootDirectory))
        {
            throw new InvalidOperationException(
                "FileEventStore requires a non-empty root directory.");
        }

        _rootDirectory = Path.GetFullPath(options.RootDirectory);
        Directory.CreateDirectory(_rootDirectory);
        _logger = logger ?? NullLogger<FileEventStore>.Instance;
    }

    public async Task<long> AppendAsync(
        string agentId,
        IEnumerable<StateEvent> events,
        long expectedVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();

        var pendingEvents = events.Select(CloneEvent).ToList();
        if (pendingEvents.Count == 0)
            return await GetVersionAsync(agentId, ct);

        var gate = _agentLocks.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            var stream = ReadStream(agentId, ct);
            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");
            }

            stream.AddRange(pendingEvents);
            WriteStream(agentId, stream, ct);

            var latestVersion = stream[^1].Version;
            _logger.LogDebug(
                "File event-store append completed. agentId={AgentId} appended={Count} version={Version}",
                agentId,
                pendingEvents.Count,
                latestVersion);
            return latestVersion;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<StateEvent>> GetEventsAsync(
        string agentId,
        long? fromVersion = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        var gate = _agentLocks.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            var stream = ReadStream(agentId, ct);
            var filtered = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value)
                : stream;
            return filtered.Select(CloneEvent).ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        var gate = _agentLocks.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            var stream = ReadStream(agentId, ct);
            return stream.Count == 0 ? 0 : stream[^1].Version;
        }
        finally
        {
            gate.Release();
        }
    }

    private List<StateEvent> ReadStream(string agentId, CancellationToken ct)
    {
        var path = GetStreamPath(agentId);
        if (!File.Exists(path))
            return [];

        var result = new List<StateEvent>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            var payloadLength = reader.ReadInt32();
            if (payloadLength <= 0)
            {
                throw new InvalidOperationException(
                    $"Corrupted event stream for agent '{agentId}': invalid payload length {payloadLength}.");
            }

            var payload = reader.ReadBytes(payloadLength);
            if (payload.Length != payloadLength)
            {
                throw new InvalidOperationException(
                    $"Corrupted event stream for agent '{agentId}': truncated payload.");
            }

            result.Add(StateEvent.Parser.ParseFrom(payload));
        }

        return result;
    }

    private void WriteStream(string agentId, IReadOnlyList<StateEvent> stream, CancellationToken ct)
    {
        var path = GetStreamPath(agentId);
        var tempPath = path + ".tmp";

        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(fileStream, Encoding.UTF8, leaveOpen: false))
        {
            foreach (var evt in stream)
            {
                ct.ThrowIfCancellationRequested();
                var payload = evt.ToByteArray();
                writer.Write(payload.Length);
                writer.Write(payload);
            }
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string GetStreamPath(string agentId)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(agentId))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return Path.Combine(_rootDirectory, encoded + ".events");
    }

    private static StateEvent CloneEvent(StateEvent evt) => evt.Clone();
}
