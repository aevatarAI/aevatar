namespace Aevatar.Foundation.Runtime.Persistence;

/// <summary>
/// File-backed event-store options.
/// </summary>
public sealed class FileEventStoreOptions
{
    /// <summary>
    /// Root directory used to persist per-agent event streams.
    /// </summary>
    public string RootDirectory { get; set; } = Path.Combine(
        AppContext.BaseDirectory,
        ".aevatar",
        "event-store");
}
