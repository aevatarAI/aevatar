using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Aevatar.CQRS.Sagas.Abstractions.State;
using Aevatar.CQRS.Sagas.Runtime.FileSystem.Storage;

namespace Aevatar.CQRS.Sagas.Runtime.FileSystem.Stores;

internal sealed class FileSystemSagaRepository : ISagaRepository
{
    private readonly SagaPathResolver _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public FileSystemSagaRepository(SagaPathResolver paths)
    {
        _paths = paths;
    }

    public async Task<ISagaState?> LoadAsync(
        string sagaName,
        string correlationId,
        Type stateType,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(stateType);

        var path = BuildPath(sagaName, correlationId);
        var document = await JsonFileStorage.ReadAsync<SagaStateDocument>(path, _jsonOptions, ct);
        if (document == null)
            return null;

        var state = document.State.Deserialize(stateType, _jsonOptions);
        return state as ISagaState;
    }

    public async Task SaveAsync(
        string sagaName,
        ISagaState state,
        Type stateType,
        int? expectedVersion = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stateType);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.CorrelationId);

        var document = new SagaStateDocument
        {
            SagaName = sagaName,
            CorrelationId = state.CorrelationId,
            StateType = stateType.AssemblyQualifiedName ?? stateType.FullName ?? stateType.Name,
            UpdatedAt = DateTimeOffset.UtcNow,
            State = JsonSerializer.SerializeToElement(state, stateType, _jsonOptions),
        };

        var path = BuildPath(sagaName, state.CorrelationId);
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                16 * 1024,
                useAsync: true);

            SagaStateDocument? existing = null;
            if (stream.Length > 0)
            {
                existing = await JsonSerializer.DeserializeAsync<SagaStateDocument>(stream, _jsonOptions, ct);
            }

            if (expectedVersion.HasValue)
            {
                var actualVersion = ResolveVersion(existing, stateType);
                if (actualVersion != expectedVersion.Value)
                {
                    throw new SagaConcurrencyException(
                        $"Saga state version conflict. saga={sagaName}, correlationId={state.CorrelationId}, expected={expectedVersion.Value}, actual={actualVersion}");
                }
            }

            stream.Position = 0;
            stream.SetLength(0);
            await JsonSerializer.SerializeAsync(stream, document, _jsonOptions, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ISagaState>> ListAsync(
        string sagaName,
        Type stateType,
        int take = 100,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
        ArgumentNullException.ThrowIfNull(stateType);

        var folder = _paths.EnsureSagaFolder(sagaName);
        var boundedTake = Math.Clamp(take, 1, 1000);
        var files = Directory
            .EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(boundedTake)
            .ToList();

        var states = new List<ISagaState>(files.Count);
        foreach (var file in files)
        {
            var document = await JsonFileStorage.ReadAsync<SagaStateDocument>(file, _jsonOptions, ct);
            if (document == null)
                continue;

            var state = document.State.Deserialize(stateType, _jsonOptions) as ISagaState;
            if (state != null)
                states.Add(state);
        }

        return states;
    }

    private string BuildPath(string sagaName, string correlationId)
    {
        var folder = _paths.EnsureSagaFolder(sagaName);
        var hash = ToHash(correlationId);
        return Path.Combine(folder, $"{hash}.json");
    }

    private int ResolveVersion(
        SagaStateDocument? existing,
        Type stateType)
    {
        if (existing == null)
            return -1;

        if (existing.State.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return -1;

        var state = existing.State.Deserialize(stateType, _jsonOptions) as ISagaState;
        return state?.Version ?? -1;
    }

    private static string ToHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class SagaStateDocument
    {
        public string SagaName { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string StateType { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
        public JsonElement State { get; set; }
    }
}
