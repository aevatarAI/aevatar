using System.Text.Json;

namespace Aevatar.CQRS.Sagas.Runtime.FileSystem.Storage;

internal static class JsonFileStorage
{
    public static async Task WriteAsync<T>(
        string path,
        T model,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, model, options, ct);
    }

    public static async Task<T?> ReadAsync<T>(
        string path,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(path))
            return default;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, options, ct);
    }
}
