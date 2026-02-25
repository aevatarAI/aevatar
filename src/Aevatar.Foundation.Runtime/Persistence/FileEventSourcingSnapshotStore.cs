using System.Collections.Concurrent;
using System.Text;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Persistence;

/// <summary>
/// File-backed snapshot store for event sourcing snapshots.
/// </summary>
public sealed class FileEventSourcingSnapshotStore<TState> : IEventSourcingSnapshotStore<TState>
    where TState : class, IMessage<TState>, new()
{
    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentLocks = new(StringComparer.Ordinal);

    public FileEventSourcingSnapshotStore(FileEventStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootDirectory))
            throw new InvalidOperationException("File snapshot store requires a non-empty root directory.");

        _rootDirectory = Path.GetFullPath(options.RootDirectory);
        Directory.CreateDirectory(_rootDirectory);
    }

    public async Task<EventSourcingSnapshot<TState>?> LoadAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        var gate = _agentLocks.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            var path = GetSnapshotPath(agentId);
            if (!File.Exists(path))
                return null;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var version = reader.ReadInt64();
            var payloadLength = reader.ReadInt32();
            if (payloadLength <= 0)
                throw new InvalidOperationException($"Corrupted snapshot for '{agentId}': invalid payload length {payloadLength}.");

            var payload = reader.ReadBytes(payloadLength);
            if (payload.Length != payloadLength)
                throw new InvalidOperationException($"Corrupted snapshot for '{agentId}': truncated payload.");

            var state = new TState();
            state.MergeFrom(payload);
            return new EventSourcingSnapshot<TState>(state, version);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(string agentId, EventSourcingSnapshot<TState> snapshot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        var gate = _agentLocks.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            var path = GetSnapshotPath(agentId);
            var tempPath = path + ".tmp";
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                var payload = snapshot.State.ToByteArray();
                writer.Write(snapshot.Version);
                writer.Write(payload.Length);
                writer.Write(payload);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetSnapshotPath(string agentId)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(agentId))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return Path.Combine(_rootDirectory, encoded + ".snapshot");
    }
}
