namespace Sisyphus.Application.Services;

/// <summary>
/// Configuration for the chrono-graph instances used by Sisyphus.
/// Values must be graph UUIDs.
/// </summary>
public sealed class SisyphusGraphOptions
{
    public const string SectionName = "Sisyphus:Graph";

    /// <summary>Graph UUID for read operations (snapshot, traverse).</summary>
    public string? ReadGraphId { get; set; }

    /// <summary>Graph UUID for write operations (create nodes/edges).</summary>
    public string? WriteGraphId { get; set; }
}
