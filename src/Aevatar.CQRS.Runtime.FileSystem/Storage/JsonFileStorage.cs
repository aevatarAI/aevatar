using System.Text.Json;

namespace Aevatar.CQRS.Runtime.FileSystem.Storage;

internal static class JsonFileStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            useAsync: true);

        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return default;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            useAsync: true);

        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }
}
