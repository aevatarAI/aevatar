using System.Collections.Concurrent;
using System.Text;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Runtime.Persistence;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Implementations.Local.ActivationIndex;

internal interface ILocalActorRuntimeEnvelopeStore
{
    Task<RuntimeActorStateEnvelope?> GetAsync(
        string actorId,
        CancellationToken ct = default);

    Task<RuntimeActorStateEnvelope?> GetForActivationAsync(
        string actorId,
        string stateContractTypeName,
        CancellationToken ct = default);

    Task<bool> CompareExchangeAsync(
        string actorId,
        RuntimeActorStateEnvelope? expected,
        RuntimeActorStateEnvelope replacement,
        CancellationToken ct = default);

    Task DeleteAsync(string actorId, CancellationToken ct = default);
}

internal sealed class InMemoryLocalActorRuntimeEnvelopeStore
    : ILocalActorRuntimeEnvelopeStore
{
    private readonly ConcurrentDictionary<string, RuntimeActorStateEnvelope> _rows =
        new(StringComparer.Ordinal);

    public Task<RuntimeActorStateEnvelope?> GetAsync(
        string actorId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_rows.GetValueOrDefault(actorId)?.Clone());
    }

    public Task<RuntimeActorStateEnvelope?> GetForActivationAsync(
        string actorId,
        string stateContractTypeName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateContractTypeName);
        return GetAsync(actorId, ct);
    }

    public Task<bool> CompareExchangeAsync(
        string actorId,
        RuntimeActorStateEnvelope? expected,
        RuntimeActorStateEnvelope replacement,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(replacement);
        ct.ThrowIfCancellationRequested();

        if (expected == null)
            return Task.FromResult(_rows.TryAdd(actorId, replacement.Clone()));

        return Task.FromResult(
            _rows.TryUpdate(actorId, replacement.Clone(), expected));
    }

    public Task DeleteAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        _rows.TryRemove(actorId, out _);
        return Task.CompletedTask;
    }
}

internal sealed class FileLocalActorRuntimeEnvelopeStore
    : ILocalActorRuntimeEnvelopeStore
{
    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _actorLocks =
        new(StringComparer.Ordinal);

    public FileLocalActorRuntimeEnvelopeStore(FileEventStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootDirectory))
            throw new InvalidOperationException("File runtime envelope store requires a root directory.");

        _rootDirectory = Path.GetFullPath(options.RootDirectory);
        Directory.CreateDirectory(_rootDirectory);
    }

    public async Task<RuntimeActorStateEnvelope?> GetAsync(
        string actorId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var gate = GetLock(actorId);
        await gate.WaitAsync(ct);
        try
        {
            return await ReadCoreAsync(actorId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RuntimeActorStateEnvelope?> GetForActivationAsync(
        string actorId,
        string stateContractTypeName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateContractTypeName);
        var gate = GetLock(actorId);
        await gate.WaitAsync(ct);
        try
        {
            var current = await ReadCoreAsync(actorId, ct);
            if (current != null)
                return current;

            var legacySnapshot = await ReadLegacySnapshotAsync(actorId, ct);
            if (legacySnapshot == null)
                return null;

            var imported = new RuntimeActorStateEnvelope
            {
                StateContractTypeName = stateContractTypeName.Trim(),
                StateSnapshot = ByteString.CopyFrom(legacySnapshot.Value.Payload),
                StateSnapshotVersion = legacySnapshot.Value.Version,
            };
            await WriteCoreAsync(actorId, imported, ct);
            return imported.Clone();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> CompareExchangeAsync(
        string actorId,
        RuntimeActorStateEnvelope? expected,
        RuntimeActorStateEnvelope replacement,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(replacement);
        var gate = GetLock(actorId);
        await gate.WaitAsync(ct);
        try
        {
            var current = await ReadCoreAsync(actorId, ct);
            if (!Equals(current, expected))
                return false;

            await WriteCoreAsync(actorId, replacement, ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var gate = GetLock(actorId);
        await gate.WaitAsync(ct);
        try
        {
            var path = GetPath(actorId);
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GetLock(string actorId) =>
        _actorLocks.GetOrAdd(actorId, static _ => new SemaphoreSlim(1, 1));

    private async Task<RuntimeActorStateEnvelope?> ReadCoreAsync(
        string actorId,
        CancellationToken ct)
    {
        var path = GetPath(actorId);
        if (!File.Exists(path))
            return null;
        var payload = await File.ReadAllBytesAsync(path, ct);
        return RuntimeActorStateEnvelope.Parser.ParseFrom(payload);
    }

    private async Task<LegacySnapshot?> ReadLegacySnapshotAsync(
        string actorId,
        CancellationToken ct)
    {
        var path = GetLegacySnapshotPath(actorId);
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < sizeof(long) + sizeof(int))
            throw new InvalidOperationException($"Corrupted legacy snapshot for '{actorId}': invalid header.");

        var version = reader.ReadInt64();
        if (version < 0)
            throw new InvalidOperationException($"Corrupted legacy snapshot for '{actorId}': invalid version {version}.");

        var payloadLength = reader.ReadInt32();
        if (payloadLength <= 0)
        {
            throw new InvalidOperationException(
                $"Corrupted legacy snapshot for '{actorId}': invalid payload length {payloadLength}.");
        }

        var payload = new byte[payloadLength];
        var offset = 0;
        while (offset < payloadLength)
        {
            var read = await stream.ReadAsync(payload.AsMemory(offset, payloadLength - offset), ct);
            if (read == 0)
            {
                throw new InvalidOperationException(
                    $"Corrupted legacy snapshot for '{actorId}': truncated payload.");
            }

            offset += read;
        }

        if (stream.Position != stream.Length)
            throw new InvalidOperationException($"Corrupted legacy snapshot for '{actorId}': trailing payload.");

        return new LegacySnapshot(version, payload);
    }

    private async Task WriteCoreAsync(
        string actorId,
        RuntimeActorStateEnvelope envelope,
        CancellationToken ct)
    {
        var path = GetPath(actorId);
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, envelope.ToByteArray(), ct);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private string GetPath(string actorId)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(actorId))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return Path.Combine(_rootDirectory, encoded + ".runtime-envelope.pb");
    }

    private string GetLegacySnapshotPath(string actorId)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(actorId))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return Path.Combine(_rootDirectory, encoded + ".snapshot");
    }

    private readonly record struct LegacySnapshot(long Version, byte[] Payload);
}
