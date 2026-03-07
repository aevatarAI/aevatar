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
    private const int StreamFormatMagic = 0x53464541; // AEFS
    private const int StreamFormatVersion = 1;

    private sealed class EventStreamState
    {
        public long CurrentVersion { get; set; }

        public List<StateEvent> Events { get; } = [];
    }

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
            var currentVersion = stream.CurrentVersion;
            if (currentVersion != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Optimistic concurrency conflict: expected {expectedVersion}, actual {currentVersion}");
            }

            stream.Events.AddRange(pendingEvents);
            stream.CurrentVersion = pendingEvents[^1].Version;
            WriteStream(agentId, stream, ct);

            var latestVersion = stream.CurrentVersion;
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
                ? stream.Events.Where(x => x.Version > fromVersion.Value)
                : stream.Events.AsEnumerable();
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
            return stream.CurrentVersion;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<long> DeleteEventsUpToAsync(
        string agentId,
        long toVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();
        if (toVersion <= 0)
            return 0;

        var gate = _agentLocks.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            var stream = ReadStream(agentId, ct);
            var before = stream.Events.Count;
            stream.Events.RemoveAll(x => x.Version <= toVersion);
            var removed = before - stream.Events.Count;
            if (removed > 0)
                WriteStream(agentId, stream, ct);
            return removed;
        }
        finally
        {
            gate.Release();
        }
    }

    private EventStreamState ReadStream(string agentId, CancellationToken ct)
    {
        var path = GetStreamPath(agentId);
        if (!File.Exists(path))
            return new EventStreamState();

        var result = new EventStreamState();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (stream.Length == 0)
            return result;

        var firstToken = reader.ReadInt32();
        if (firstToken == StreamFormatMagic)
        {
            if (stream.Length < sizeof(int) + sizeof(int) + sizeof(long) + sizeof(int))
            {
                throw new InvalidOperationException(
                    $"Corrupted event stream for agent '{agentId}': invalid header.");
            }

            var formatVersion = reader.ReadInt32();
            if (formatVersion != StreamFormatVersion)
            {
                throw new InvalidOperationException(
                    $"Unsupported event stream format version {formatVersion} for agent '{agentId}'.");
            }

            result.CurrentVersion = reader.ReadInt64();
            var count = reader.ReadInt32();
            if (count < 0)
            {
                throw new InvalidOperationException(
                    $"Corrupted event stream for agent '{agentId}': invalid event count {count}.");
            }

            for (var i = 0; i < count; i++)
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

                result.Events.Add(StateEvent.Parser.ParseFrom(payload));
            }

            return result;
        }

        throw new InvalidOperationException(
            $"Unsupported event stream header for agent '{agentId}'.");
    }

    private void WriteStream(string agentId, EventStreamState stream, CancellationToken ct)
    {
        var path = GetStreamPath(agentId);
        var tempPath = path + ".tmp";

        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(fileStream, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write(StreamFormatMagic);
            writer.Write(StreamFormatVersion);
            writer.Write(stream.CurrentVersion);
            writer.Write(stream.Events.Count);

            foreach (var evt in stream.Events)
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
